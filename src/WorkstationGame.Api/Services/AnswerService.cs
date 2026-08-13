using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using WorkstationGame.Api.Data;
using WorkstationGame.Api.Domain;

namespace WorkstationGame.Api.Services;

public sealed class AnswerService(GameDbContext database)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RoundLocks = new();

    public async Task<(PlayerAnswer? Answer, string? Error)> SubmitAsync(
        GameSession game,
        GameRound round,
        Player player,
        string guess,
        CancellationToken cancellationToken)
    {
        var gate = RoundLocks.GetOrAdd(round.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await database.Answers.AnyAsync(
                    x => x.GameRoundId == round.Id && x.PlayerId == player.Id,
                    cancellationToken))
            {
                return (null, "You already answered this round.");
            }

            var now = DateTimeOffset.UtcNow;
            if (game.Status != GameStatus.Live || round.Status != RoundStatus.Open || round.OpenedAt is null ||
                now > round.OpenedAt.Value.AddSeconds(round.DurationSeconds))
            {
                return (null, "This round is not accepting answers.");
            }

            if (!round.Options.Any(x => string.Equals(x.Label, guess, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "That guess is not one of the available choices.");
            }

            var isCorrect = string.Equals(round.CorrectAnswer, guess, StringComparison.OrdinalIgnoreCase);
            var rank = 0;
            var score = 0;
            if (isCorrect)
            {
                rank = await database.Answers.CountAsync(
                    x => x.GameRoundId == round.Id && x.IsCorrect,
                    cancellationToken) + 1;
                score = ScoreCalculator.ForCorrectRank(rank);
                player.TotalScore += score;
            }

            var answer = new PlayerAnswer
            {
                GameRoundId = round.Id,
                PlayerId = player.Id,
                Guess = guess.Trim(),
                IsCorrect = isCorrect,
                CorrectRank = rank,
                ScoreAwarded = score,
                SubmittedAt = now
            };
            database.Answers.Add(answer);
            await database.SaveChangesAsync(cancellationToken);
            return (answer, null);
        }
        finally
        {
            gate.Release();
        }
    }
}
