using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WorkstationGame.Api.Contracts;
using WorkstationGame.Api.Data;
using WorkstationGame.Api.Domain;
using WorkstationGame.Api.Services;

namespace WorkstationGame.Api.Endpoints;

public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var host = app.MapGroup("/api/host");
        host.AddEndpointFilter(async (context, next) =>
            HostAccess.IsAuthorized(context.HttpContext.Request, context.HttpContext.RequestServices.GetRequiredService<IConfiguration>())
                ? await next(context)
                : Results.Unauthorized());

        host.MapGet("/verify", () => Results.Ok(new { authenticated = true }));
        host.MapGet("/games", GetGamesAsync);
        host.MapPost("/games", CreateGameAsync);
        host.MapGet("/games/{gameId:guid}", GetGameAsync);
        host.MapPost("/games/{gameId:guid}/rounds", CreateRoundAsync);
        host.MapDelete("/games/{gameId:guid}/rounds/{roundId:guid}", DeleteRoundAsync);
        host.MapPost("/games/{gameId:guid}/rounds/{roundId:guid}/open", OpenRoundAsync);
        host.MapPost("/games/{gameId:guid}/rounds/{roundId:guid}/reveal", RevealRoundAsync);
        host.MapPost("/games/{gameId:guid}/finish", FinishGameAsync);
        host.MapPost("/storage/images", UploadImageAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> GetGamesAsync(GameDbContext database, CancellationToken cancellationToken)
    {
        var games = await database.Games.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Title,
                status = x.Status.ToString(),
                x.CreatedAt,
                playerCount = x.Players.Count,
                roundCount = x.Rounds.Count
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(games);
    }

    private static async Task<IResult> CreateGameAsync(
        CreateGameRequest request,
        GameDbContext database,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120)
        {
            return Results.BadRequest(new { message = "Enter a title between 1 and 120 characters." });
        }

        string code;
        do
        {
            code = CreateCode();
        } while (await database.Games.AnyAsync(x => x.Code == code, cancellationToken));

        var game = new GameSession { Code = code, Title = title };
        database.Games.Add(game);
        await database.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/host/games/{game.Id}", new { game.Id, game.Code, game.Title });
    }

    private static async Task<IResult> GetGameAsync(
        Guid gameId,
        GameDbContext database,
        CancellationToken cancellationToken)
    {
        var game = await database.Games.AsNoTracking()
            .Include(x => x.Players)
            .Include(x => x.Rounds).ThenInclude(x => x.Options)
            .Include(x => x.Rounds).ThenInclude(x => x.Answers)
            .SingleOrDefaultAsync(x => x.Id == gameId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }

        return Results.Ok(new
        {
            game.Id,
            game.Code,
            game.Title,
            status = game.Status.ToString(),
            game.CreatedAt,
            players = game.Players.OrderByDescending(x => x.TotalScore).ThenBy(x => x.JoinedAt).Select((x, index) => new
            {
                rank = index + 1,
                x.Id,
                x.DisplayName,
                x.TotalScore,
                x.JoinedAt
            }),
            rounds = game.Rounds.OrderBy(x => x.Position).Select(x => new
            {
                x.Id,
                x.Position,
                x.CorrectAnswer,
                x.FunFact,
                x.ImagePath,
                x.ExternalImageUrl,
                x.DurationSeconds,
                status = x.Status.ToString(),
                x.OpenedAt,
                x.RevealedAt,
                options = x.Options.OrderBy(o => o.Position).Select(o => o.Label),
                answerCount = x.Answers.Count,
                correctCount = x.Answers.Count(a => a.IsCorrect)
            })
        });
    }

    private static async Task<IResult> CreateRoundAsync(
        Guid gameId,
        CreateRoundRequest request,
        GameDbContext database,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var game = await database.Games.Include(x => x.Rounds).SingleOrDefaultAsync(x => x.Id == gameId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }
        if (game.Status == GameStatus.Finished)
        {
            return Results.Conflict(new { message = "A finished game cannot be edited." });
        }

        var correctAnswer = request.CorrectAnswer?.Trim();
        var options = request.Options
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(correctAnswer) || !options.Contains(correctAnswer, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { message = "The correct answer must be included in the choices." });
        }
        if (options.Count is < 2 or > 20)
        {
            return Results.BadRequest(new { message = "Each round needs between 2 and 20 unique choices." });
        }
        if (request.DurationSeconds is < 10 or > 180)
        {
            return Results.BadRequest(new { message = "Round duration must be between 10 and 180 seconds." });
        }
        if (string.IsNullOrWhiteSpace(request.ImagePath) && string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            return Results.BadRequest(new { message = "Upload an image or provide an image URL." });
        }

        var round = new GameRound
        {
            GameSessionId = game.Id,
            Position = game.Rounds.Count == 0 ? 1 : game.Rounds.Max(x => x.Position) + 1,
            CorrectAnswer = correctAnswer,
            FunFact = string.IsNullOrWhiteSpace(request.FunFact) ? null : request.FunFact.Trim(),
            ImagePath = string.IsNullOrWhiteSpace(request.ImagePath) ? null : request.ImagePath.Trim(),
            ExternalImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim(),
            DurationSeconds = request.DurationSeconds,
            Options = options.Select((label, index) => new RoundOption { Label = label, Position = index + 1 }).ToList()
        };
        database.Rounds.Add(round);
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(game.Code, cancellationToken);
        return Results.Created($"/api/host/games/{game.Id}", new { round.Id, round.Position });
    }

    private static async Task<IResult> DeleteRoundAsync(
        Guid gameId,
        Guid roundId,
        GameDbContext database,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var round = await database.Rounds.Include(x => x.GameSession).SingleOrDefaultAsync(
            x => x.Id == roundId && x.GameSessionId == gameId,
            cancellationToken);
        if (round is null)
        {
            return Results.NotFound();
        }
        if (round.Status != RoundStatus.Pending)
        {
            return Results.Conflict(new { message = "Only pending rounds can be deleted." });
        }

        var code = round.GameSession.Code;
        database.Rounds.Remove(round);
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(code, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> OpenRoundAsync(
        Guid gameId,
        Guid roundId,
        GameDbContext database,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var game = await database.Games.Include(x => x.Rounds).SingleOrDefaultAsync(x => x.Id == gameId, cancellationToken);
        var round = game?.Rounds.SingleOrDefault(x => x.Id == roundId);
        if (game is null || round is null)
        {
            return Results.NotFound(new { message = "Game or round not found." });
        }
        if (game.Status == GameStatus.Finished || round.Status != RoundStatus.Pending)
        {
            return Results.Conflict(new { message = "This round cannot be opened." });
        }
        if (game.Rounds.Any(x => x.Status == RoundStatus.Open))
        {
            return Results.Conflict(new { message = "Reveal the current round before opening another." });
        }
        var previousPending = game.Rounds.Any(x => x.Position < round.Position && x.Status != RoundStatus.Revealed);
        if (previousPending)
        {
            return Results.Conflict(new { message = "Rounds must be played in order." });
        }

        game.Status = GameStatus.Live;
        round.Status = RoundStatus.Open;
        round.OpenedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(game.Code, cancellationToken);
        return Results.Ok(new { openedAt = round.OpenedAt, round.DurationSeconds });
    }

    private static async Task<IResult> RevealRoundAsync(
        Guid gameId,
        Guid roundId,
        GameDbContext database,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var round = await database.Rounds.Include(x => x.GameSession).SingleOrDefaultAsync(
            x => x.Id == roundId && x.GameSessionId == gameId,
            cancellationToken);
        if (round is null)
        {
            return Results.NotFound();
        }
        if (round.Status != RoundStatus.Open)
        {
            return Results.Conflict(new { message = "Only an open round can be revealed." });
        }

        round.Status = RoundStatus.Revealed;
        round.RevealedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(round.GameSession.Code, cancellationToken);
        return Results.Ok(new { round.CorrectAnswer, round.FunFact });
    }

    private static async Task<IResult> FinishGameAsync(
        Guid gameId,
        GameDbContext database,
        GameNotifier notifier,
        CancellationToken cancellationToken)
    {
        var game = await database.Games.Include(x => x.Rounds).SingleOrDefaultAsync(x => x.Id == gameId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }
        if (game.Rounds.Any(x => x.Status == RoundStatus.Open))
        {
            return Results.Conflict(new { message = "Reveal the current round before finishing the game." });
        }

        game.Status = GameStatus.Finished;
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(game.Code, cancellationToken);
        return Results.Ok(new { status = game.Status.ToString() });
    }

    private static async Task<IResult> UploadImageAsync(
        IFormFile file,
        SupabaseStorageService storage,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > 10 * 1024 * 1024)
        {
            return Results.BadRequest(new { message = "Choose an image smaller than 10 MB." });
        }
        try
        {
            var path = await storage.UploadAsync(file, cancellationToken);
            return Results.Ok(new { imagePath = path });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    private static string CreateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.ToArray().Select(x => alphabet[x % alphabet.Length]).ToArray());
    }
}

