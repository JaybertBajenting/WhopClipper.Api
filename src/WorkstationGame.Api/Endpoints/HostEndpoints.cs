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
        host.MapPost("/games/{gameId:guid}/duplicate", DuplicateGameAsync);
        host.MapPost("/games/{gameId:guid}/rounds", CreateRoundAsync);
        host.MapDelete("/games/{gameId:guid}/rounds/{roundId:guid}", DeleteRoundAsync);
        host.MapPost("/games/{gameId:guid}/start", StartGameAsync);
        host.MapPost("/games/{gameId:guid}/pause", PauseGameAsync);
        host.MapPost("/games/{gameId:guid}/resume", ResumeGameAsync);
        host.MapPost("/games/{gameId:guid}/skip", SkipPhaseAsync);
        host.MapPost("/games/{gameId:guid}/end", EndGameAsync);
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
            game.RevealDurationSeconds,
            game.PausedAt,
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

    private static async Task<IResult> DuplicateGameAsync(
        Guid gameId,
        DuplicateGameRequest request,
        GameDbContext database,
        CancellationToken cancellationToken)
    {
        var source = await database.Games.AsNoTracking()
            .Include(x => x.Rounds).ThenInclude(x => x.Options)
            .SingleOrDefaultAsync(x => x.Id == gameId, cancellationToken);
        if (source is null)
        {
            return Results.NotFound(new { message = "Game not found." });
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? source.Title : request.Title.Trim();
        if (title.Length > 120)
        {
            return Results.BadRequest(new { message = "Enter a title between 1 and 120 characters." });
        }

        string code;
        do
        {
            code = CreateCode();
        } while (await database.Games.AnyAsync(x => x.Code == code, cancellationToken));

        var duplicate = GameCloneFactory.Clone(source, code, title);
        database.Games.Add(duplicate);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/host/games/{duplicate.Id}", new
        {
            duplicate.Id,
            duplicate.Code,
            duplicate.Title,
            roundCount = duplicate.Rounds.Count
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
        if (game.Status != GameStatus.Lobby)
        {
            return Results.Conflict(new { message = "Rounds can only be edited before the game starts." });
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
        if (round.GameSession.Status != GameStatus.Lobby || round.Status != RoundStatus.Pending)
        {
            return Results.Conflict(new { message = "Rounds can only be deleted before the game starts." });
        }

        var code = round.GameSession.Code;
        database.Rounds.Remove(round);
        await database.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(code, cancellationToken);
        return Results.NoContent();
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

    private static async Task<IResult> StartGameAsync(
        Guid gameId,
        GameLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.StartAsync(gameId, cancellationToken));

    private static async Task<IResult> PauseGameAsync(
        Guid gameId,
        GameLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.PauseAsync(gameId, cancellationToken));

    private static async Task<IResult> ResumeGameAsync(
        Guid gameId,
        GameLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.ResumeAsync(gameId, cancellationToken));

    private static async Task<IResult> SkipPhaseAsync(
        Guid gameId,
        GameLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.SkipAsync(gameId, cancellationToken));

    private static async Task<IResult> EndGameAsync(
        Guid gameId,
        GameLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.EndAsync(gameId, cancellationToken));

    private static IResult ToResult((bool Success, string? Error) result) =>
        result.Success ? Results.Ok(new { success = true }) : Results.Conflict(new { message = result.Error });

    private static string CreateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.ToArray().Select(x => alphabet[x % alphabet.Length]).ToArray());
    }
}
