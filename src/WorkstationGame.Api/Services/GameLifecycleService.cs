using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using WorkstationGame.Api.Data;
using WorkstationGame.Api.Domain;

namespace WorkstationGame.Api.Services;

public sealed class GameLifecycleService(GameDbContext database, GameNotifier notifier)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> GameLocks = new();

    public async Task<(bool Success, string? Error)> StartAsync(Guid gameId, CancellationToken cancellationToken)
    {
        return await WithLockAsync(gameId, async () =>
        {
            var game = await LoadGameAsync(gameId, cancellationToken);
            if (game is null) return (false, "Game not found.");
            if (game.Status != GameStatus.Lobby) return (false, "Only a lobby game can be started.");
            var firstRound = game.Rounds.OrderBy(x => x.Position).FirstOrDefault();
            if (firstRound is null) return (false, "Add at least one round before starting.");

            game.Status = GameStatus.Live;
            Open(firstRound, DateTimeOffset.UtcNow);
            await SaveAndNotifyAsync(game, cancellationToken);
            return (true, null);
        }, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> PauseAsync(Guid gameId, CancellationToken cancellationToken)
    {
        return await WithLockAsync(gameId, async () =>
        {
            var game = await LoadGameAsync(gameId, cancellationToken);
            if (game is null) return (false, "Game not found.");
            if (game.Status != GameStatus.Live) return (false, "Only a live game can be paused.");

            game.Status = GameStatus.Paused;
            game.PausedAt = DateTimeOffset.UtcNow;
            await SaveAndNotifyAsync(game, cancellationToken);
            return (true, null);
        }, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> ResumeAsync(Guid gameId, CancellationToken cancellationToken)
    {
        return await WithLockAsync(gameId, async () =>
        {
            var game = await LoadGameAsync(gameId, cancellationToken);
            if (game is null) return (false, "Game not found.");
            if (game.Status != GameStatus.Paused || game.PausedAt is null)
                return (false, "Only a paused game can be resumed.");

            var pauseDuration = DateTimeOffset.UtcNow - game.PausedAt.Value;
            var current = CurrentRound(game);
            if (current?.Status == RoundStatus.Open && current.OpenedAt.HasValue)
                current.OpenedAt = current.OpenedAt.Value.Add(pauseDuration);
            else if (current?.Status == RoundStatus.Revealed && current.RevealedAt.HasValue)
                current.RevealedAt = current.RevealedAt.Value.Add(pauseDuration);

            game.Status = GameStatus.Live;
            game.PausedAt = null;
            await SaveAndNotifyAsync(game, cancellationToken);
            return (true, null);
        }, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> SkipAsync(Guid gameId, CancellationToken cancellationToken)
    {
        return await WithLockAsync(gameId, async () =>
        {
            var game = await LoadGameAsync(gameId, cancellationToken);
            if (game is null) return (false, "Game not found.");
            if (game.Status != GameStatus.Live) return (false, "Resume the game before skipping.");

            var current = CurrentRound(game);
            if (current?.Status == RoundStatus.Open)
            {
                Reveal(current, DateTimeOffset.UtcNow);
            }
            else if (current?.Status == RoundStatus.Revealed)
            {
                AdvanceAfterReveal(game, DateTimeOffset.UtcNow);
            }
            else
            {
                return (false, "There is no active phase to skip.");
            }

            await SaveAndNotifyAsync(game, cancellationToken);
            return (true, null);
        }, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> EndAsync(Guid gameId, CancellationToken cancellationToken)
    {
        return await WithLockAsync(gameId, async () =>
        {
            var game = await LoadGameAsync(gameId, cancellationToken);
            if (game is null) return (false, "Game not found.");
            if (game.Status == GameStatus.Finished) return (false, "The game is already finished.");

            var openRound = game.Rounds.SingleOrDefault(x => x.Status == RoundStatus.Open);
            if (openRound is not null) Reveal(openRound, DateTimeOffset.UtcNow);
            game.Status = GameStatus.Finished;
            game.PausedAt = null;
            await SaveAndNotifyAsync(game, cancellationToken);
            return (true, null);
        }, cancellationToken);
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var liveGameIds = await database.Games.AsNoTracking()
            .Where(x => x.Status == GameStatus.Live)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var gameId in liveGameIds)
        {
            await WithLockAsync(gameId, async () =>
            {
                var game = await LoadGameAsync(gameId, cancellationToken);
                if (game?.Status != GameStatus.Live) return (true, null);

                var now = DateTimeOffset.UtcNow;
                var current = CurrentRound(game);
                var changed = false;
                if (current?.Status == RoundStatus.Open && current.OpenedAt.HasValue &&
                    now >= current.OpenedAt.Value.AddSeconds(current.DurationSeconds))
                {
                    Reveal(current, now);
                    changed = true;
                }
                else if (current?.Status == RoundStatus.Revealed && current.RevealedAt.HasValue &&
                         now >= current.RevealedAt.Value.AddSeconds(game.RevealDurationSeconds))
                {
                    AdvanceAfterReveal(game, now);
                    changed = true;
                }

                if (changed) await SaveAndNotifyAsync(game, cancellationToken);
                return (true, null);
            }, cancellationToken);
        }
    }

    private Task<GameSession?> LoadGameAsync(Guid gameId, CancellationToken cancellationToken) =>
        database.Games.Include(x => x.Rounds).SingleOrDefaultAsync(x => x.Id == gameId, cancellationToken);

    private async Task SaveAndNotifyAsync(GameSession game, CancellationToken cancellationToken)
    {
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(game.Code, cancellationToken);
    }

    private static GameRound? CurrentRound(GameSession game) =>
        game.Rounds.Where(x => x.Status is RoundStatus.Open or RoundStatus.Revealed)
            .OrderByDescending(x => x.Position)
            .FirstOrDefault();

    internal static void Open(GameRound round, DateTimeOffset now)
    {
        round.Status = RoundStatus.Open;
        round.OpenedAt = now;
        round.RevealedAt = null;
    }

    internal static void Reveal(GameRound round, DateTimeOffset now)
    {
        round.Status = RoundStatus.Revealed;
        round.RevealedAt = now;
    }

    internal static void AdvanceAfterReveal(GameSession game, DateTimeOffset now)
    {
        var next = game.Rounds.Where(x => x.Status == RoundStatus.Pending).OrderBy(x => x.Position).FirstOrDefault();
        if (next is null)
        {
            game.Status = GameStatus.Finished;
            game.PausedAt = null;
            return;
        }
        Open(next, now);
    }

    private static async Task<(bool Success, string? Error)> WithLockAsync(
        Guid gameId,
        Func<Task<(bool Success, string? Error)>> action,
        CancellationToken cancellationToken)
    {
        var gate = GameLocks.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { gate.Release(); }
    }
}
