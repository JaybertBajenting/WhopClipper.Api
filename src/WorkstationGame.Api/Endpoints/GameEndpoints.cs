using Microsoft.EntityFrameworkCore;
using WorkstationGame.Api.Contracts;
using WorkstationGame.Api.Data;
using WorkstationGame.Api.Domain;
using WorkstationGame.Api.Services;

namespace WorkstationGame.Api.Endpoints;

public static class GameEndpoints
{
    public static IEndpointRouteBuilder MapGameEndpoints(this IEndpointRouteBuilder app)
    {
        var games = app.MapGroup("/api/games");

        games.MapGet("/{code}", GetGameSummaryAsync);
        games.MapPost("/{code}/join", JoinGameAsync);
        games.MapGet("/{code}/state", GetPlayerStateAsync);
        games.MapPost("/{code}/answers", SubmitAnswerAsync);
        games.MapGet("/{code}/leaderboard", GetLeaderboardAsync);
        games.MapGet("/{code}/rounds/{roundId:guid}/image", GetRoundImageAsync);

        return app;
    }

    private static async Task<IResult> GetGameSummaryAsync(
        string code,
        GameDbContext database,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeCode(code);
        var game = await database.Games.AsNoTracking()
            .Where(x => x.Code == normalizedCode)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Title,
                Status = x.Status.ToString(),
                PlayerCount = x.Players.Count,
                RoundCount = x.Rounds.Count
            })
            .SingleOrDefaultAsync(cancellationToken);
        return game is null ? Results.NotFound(new { message = "Game not found." }) : Results.Ok(game);
    }

    private static async Task<IResult> JoinGameAsync(
        string code,
        JoinGameRequest request,
        GameDbContext database,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 40)
        {
            return Results.BadRequest(new { message = "Enter a name between 1 and 40 characters." });
        }

        var game = await database.Games.SingleOrDefaultAsync(x => x.Code == NormalizeCode(code), cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }
        if (game.Status == GameStatus.Finished)
        {
            return Results.Conflict(new { message = "This game has already finished." });
        }

        var normalizedName = displayName.ToUpperInvariant();
        if (await database.Players.AnyAsync(
                x => x.GameSessionId == game.Id && x.NormalizedName == normalizedName,
                cancellationToken))
        {
            return Results.Conflict(new { message = "That display name is already in use for this game." });
        }

        var token = PlayerTokens.Create();
        var player = new Player
        {
            GameSessionId = game.Id,
            DisplayName = displayName,
            NormalizedName = normalizedName,
            TokenHash = PlayerTokens.Hash(token)
        };
        database.Players.Add(player);
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(game.Code, cancellationToken);

        return Results.Ok(new
        {
            playerId = player.Id,
            playerToken = token,
            gameCode = game.Code,
            player.DisplayName
        });
    }

    private static async Task<IResult> GetPlayerStateAsync(
        string code,
        Guid? playerId,
        string? playerToken,
        GameDbContext database,
        CancellationToken cancellationToken)
    {
        var game = await database.Games.AsNoTracking()
            .Include(x => x.Players)
            .Include(x => x.Rounds).ThenInclude(x => x.Options)
            .Include(x => x.Rounds).ThenInclude(x => x.Answers)
            .SingleOrDefaultAsync(x => x.Code == NormalizeCode(code), cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }

        Player? player = null;
        if (playerId.HasValue)
        {
            player = game.Players.SingleOrDefault(x => x.Id == playerId.Value);
            if (player is null || string.IsNullOrWhiteSpace(playerToken) ||
                !PlayerTokens.Matches(playerToken, player.TokenHash))
            {
                return Results.Unauthorized();
            }
        }

        var round = game.Rounds
            .Where(x => x.Status is RoundStatus.Open or RoundStatus.Revealed)
            .OrderByDescending(x => x.Position)
            .FirstOrDefault();
        var now = DateTimeOffset.UtcNow;
        var isAcceptingAnswers = round?.Status == RoundStatus.Open && round.OpenedAt.HasValue &&
                                 now <= round.OpenedAt.Value.AddSeconds(round.DurationSeconds);
        var ownAnswer = player is null || round is null
            ? null
            : round.Answers.SingleOrDefault(x => x.PlayerId == player.Id);
        var showAnswer = round?.Status == RoundStatus.Revealed || game.Status == GameStatus.Finished;

        var leaderboard = game.Players
            .OrderByDescending(x => x.TotalScore)
            .ThenBy(x => x.JoinedAt)
            .Select((x, index) => new
            {
                rank = index + 1,
                playerId = x.Id,
                x.DisplayName,
                x.TotalScore
            })
            .Take(20)
            .ToList();

        return Results.Ok(new
        {
            game = new
            {
                game.Id,
                game.Code,
                game.Title,
                status = game.Status.ToString(),
                playerCount = game.Players.Count,
                roundCount = game.Rounds.Count
            },
            player = player is null ? null : new { player.Id, player.DisplayName, player.TotalScore },
            round = round is null ? null : new
            {
                round.Id,
                round.Position,
                status = round.Status.ToString(),
                round.DurationSeconds,
                round.OpenedAt,
                isAcceptingAnswers,
                imageUrl = $"/api/games/{game.Code}/rounds/{round.Id}/image",
                options = round.Options.OrderBy(x => x.Position).Select(x => x.Label),
                correctAnswer = showAnswer ? round.CorrectAnswer : null,
                funFact = showAnswer ? round.FunFact : null,
                answerCount = round.Answers.Count,
                ownAnswer = ownAnswer is null ? null : new
                {
                    ownAnswer.Guess,
                    isCorrect = showAnswer ? ownAnswer.IsCorrect : (bool?)null,
                    scoreAwarded = showAnswer ? ownAnswer.ScoreAwarded : (int?)null,
                    correctRank = showAnswer && ownAnswer.IsCorrect ? ownAnswer.CorrectRank : (int?)null
                }
            },
            leaderboard
        });
    }

    private static async Task<IResult> SubmitAnswerAsync(
        string code,
        SubmitAnswerRequest request,
        GameDbContext database,
        AnswerService answerService,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var game = await database.Games.SingleOrDefaultAsync(x => x.Code == NormalizeCode(code), cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }

        var player = await database.Players.SingleOrDefaultAsync(
            x => x.Id == request.PlayerId && x.GameSessionId == game.Id,
            cancellationToken);
        if (player is null || !PlayerTokens.Matches(request.PlayerToken, player.TokenHash))
        {
            return Results.Unauthorized();
        }

        var round = await database.Rounds.Include(x => x.Options).SingleOrDefaultAsync(
            x => x.Id == request.RoundId && x.GameSessionId == game.Id,
            cancellationToken);
        if (round is null)
        {
            return Results.NotFound(new { message = "Round not found." });
        }

        var (answer, error) = await answerService.SubmitAsync(game, round, player, request.Guess, cancellationToken);
        if (answer is null)
        {
            return Results.Conflict(new { message = error });
        }

        await notifier.NotifyAsync(game.Code, cancellationToken);
        return Results.Ok(new { accepted = true, submittedAt = answer.SubmittedAt });
    }

    private static async Task<IResult> GetLeaderboardAsync(
        string code,
        GameDbContext database,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeCode(code);
        var game = await database.Games.AsNoTracking().SingleOrDefaultAsync(x => x.Code == normalizedCode, cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }

        var players = await database.Players.AsNoTracking()
            .Where(x => x.GameSessionId == game.Id)
            .OrderByDescending(x => x.TotalScore)
            .ThenBy(x => x.JoinedAt)
            .Select(x => new { playerId = x.Id, x.DisplayName, x.TotalScore })
            .Take(50)
            .ToListAsync(cancellationToken);
        return Results.Ok(players.Select((x, index) => new
        {
            rank = index + 1,
            x.playerId,
            x.DisplayName,
            x.TotalScore
        }));
    }

    private static async Task<IResult> GetRoundImageAsync(
        string code,
        Guid roundId,
        GameDbContext database,
        SupabaseStorageService storage,
        CancellationToken cancellationToken)
    {
        var round = await database.Rounds.AsNoTracking()
            .Include(x => x.GameSession)
            .SingleOrDefaultAsync(x => x.Id == roundId && x.GameSession.Code == NormalizeCode(code), cancellationToken);
        if (round is null || round.Status == RoundStatus.Pending)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(round.ExternalImageUrl))
        {
            return Results.Redirect(round.ExternalImageUrl);
        }
        if (string.IsNullOrWhiteSpace(round.ImagePath))
        {
            return Results.NotFound();
        }

        try
        {
            return Results.Redirect(await storage.CreateSignedUrlAsync(round.ImagePath, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
}

