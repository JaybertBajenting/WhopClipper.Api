using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

public static class PublishingEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var publishing = api.MapGroup("/publishing").WithTags("Publishing");

        publishing.MapGet("/session", (HttpContext context, LocalPublishingRequestGuard guard) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new PublishingSessionDto(guard.Nonce, LocalPublishingRequestGuard.HeaderName));
        });
        publishing.MapGet("/platforms", () => Results.Ok(PlatformPublishingCatalog.Platforms
            .Select(PlatformPublishingCatalog.Guide)));
        publishing.MapGet("/platforms/{platform}", (PlatformKind platform) =>
            PlatformPublishingCatalog.IsPublishingPlatform(platform)
                ? Results.Ok(PlatformPublishingCatalog.Guide(platform))
                : Results.NotFound());

        api.MapGet("/campaigns/{campaignId:guid}/publishing/account-kits",
            async (Guid campaignId, PlatformAccountKitService service, CancellationToken ct) =>
            {
                var kits = await service.GetAllAsync(campaignId, ct);
                return kits is null ? Results.NotFound() : Results.Ok(kits);
            }).WithTags("Publishing");
        api.MapGet("/campaigns/{campaignId:guid}/publishing/account-kits/{platform}",
            async (Guid campaignId, PlatformKind platform, PlatformAccountKitService service, CancellationToken ct) =>
            {
                var kit = await service.GetAsync(campaignId, platform, ct);
                return kit is null ? Results.NotFound() : Results.Ok(kit);
            }).WithTags("Publishing");
        api.MapPut("/campaigns/{campaignId:guid}/publishing/account-kits/{platform}",
            async (Guid campaignId, PlatformKind platform, SavePlatformAccountKitRequest request,
                HttpContext context, LocalPublishingRequestGuard guard, PlatformAccountKitService service,
                CancellationToken ct) =>
            {
                if (!guard.IsValid(context)) return Forbidden();
                var result = await service.SaveAsync(campaignId, platform, request.Fields, ct);
                if (result.Errors is not null) return Results.ValidationProblem(result.Errors);
                return result.Kit is null ? Results.NotFound() : Results.Ok(result.Kit);
            }).WithTags("Publishing");
        api.MapPost("/campaigns/{campaignId:guid}/publishing/account-kits/{platform}/regenerate-identity",
            async (Guid campaignId, PlatformKind platform, HttpContext context,
                LocalPublishingRequestGuard guard, PlatformAccountKitService service, CancellationToken ct) =>
            {
                if (!guard.IsValid(context)) return Forbidden();
                var kit = await service.RegenerateIdentityAsync(campaignId, platform, ct);
                return kit is null ? Results.NotFound() : Results.Ok(kit);
            }).WithTags("Publishing");

        publishing.MapGet("/connections", async (PlatformConnectionService service, CancellationToken ct) =>
            Results.Ok(await service.GetAllAsync(ct)));
        // connectionId names one page of a platform that can have several (YouTube). Left out, it is the first.
        publishing.MapGet("/connections/{platform}", async (PlatformKind platform, Guid? connectionId,
            PlatformConnectionService service, CancellationToken ct) =>
            PlatformPublishingCatalog.IsPublishingPlatform(platform)
                ? Results.Ok(await service.GetAsync(platform, connectionId, ct))
                : Results.NotFound());
        publishing.MapPut("/connections/{platform}", async (PlatformKind platform, Guid? connectionId,
            SavePlatformConnectionRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            PlatformConnectionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var result = await service.SaveAsync(platform, connectionId, request, ct);
            return result.Errors is not null ? Results.ValidationProblem(result.Errors) : Results.Ok(result.Connection);
        });
        publishing.MapDelete("/connections/{platform}", async (PlatformKind platform, Guid? connectionId, HttpContext context,
            LocalPublishingRequestGuard guard, PlatformConnectionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var (found, blocked) = await service.DeleteAsync(platform, connectionId, ct);
            if (blocked is not null) return Results.Problem(title: "This page is still in use", detail: blocked, statusCode: StatusCodes.Status409Conflict);
            return found ? Results.NoContent() : Results.NotFound();
        });
        publishing.MapPost("/connections/{platform}/validate", async (PlatformKind platform, Guid? connectionId, HttpContext context,
            LocalPublishingRequestGuard guard, PlatformConnectionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            if (!PlatformPublishingCatalog.IsPublishingPlatform(platform)) return Results.NotFound();
            return Results.Ok(await service.VerifyAsync(platform, connectionId, ct));
        });
        publishing.MapPost("/connections/YouTubeShorts/pages", async (AddYouTubePageRequest request, HttpContext context,
            LocalPublishingRequestGuard guard, PlatformConnectionService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var result = await service.AddYouTubePageAsync(request.AccountId, request.CopyClientFrom, ct);
            return result.Errors is not null ? Results.ValidationProblem(result.Errors) : Results.Ok(result.Connection);
        });
        publishing.MapPost("/connections/YouTubeShorts/oauth/start", async (Guid? connectionId, HttpContext context,
            LocalPublishingRequestGuard guard, YouTubeOAuthService oauth, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var port = context.Connection.LocalPort;
            if (port is < 1 or > 65535) return Results.Problem("The local callback port is invalid.", statusCode: 500);
            var redirectUri = $"http://127.0.0.1:{port}/api/publishing/oauth/youtube/callback";
            try { return Results.Ok(await oauth.StartAsync(redirectUri, connectionId, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or PublishingApiException)
            {
                return SafeProblem(ex.Message, StatusCodes.Status400BadRequest);
            }
        });
        publishing.MapGet("/oauth/youtube/callback", async (string? code, string? state, string? error,
            YouTubeOAuthService oauth, YouTubeScheduleService schedule, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return OAuthPage(false, "Google authorization was cancelled or denied. Return to Whop Clipper and try again.", 400);
            if (string.IsNullOrWhiteSpace(code) || code.Length > 8_192 || string.IsNullOrWhiteSpace(state) || state.Length > 1_024)
                return OAuthPage(false, "The local OAuth callback was incomplete or invalid. Return to Whop Clipper and try again.", 400);
            try
            {
                await oauth.CompleteAsync(code, state, ct);
                await schedule.RetryWaitingAsync(ct);
                return OAuthPage(true, "YouTube authorization was saved securely. You can close this tab and return to Whop Clipper.", 200);
            }
            catch (YouTubePageAlreadyConnectedException ex)
            {
                // The channel name comes from YouTube, so it is encoded before it goes into the page.
                return OAuthPage(false, System.Net.WebUtility.HtmlEncode(ex.Message), 409);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PublishingApiException)
            {
                _ = PlatformConnectionService.CleanError(ex.Message);
                return OAuthPage(false, "YouTube authorization could not be completed. Return to Whop Clipper and try again.", 400);
            }
        });

        api.MapPost("/campaigns/{campaignId:guid}/publishing/jobs", async (Guid campaignId,
            CreatePublishJobRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            PublishJobService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var result = await service.CreateAsync(campaignId, request, ct);
            if (result.CampaignNotFound) return Results.NotFound();
            if (result.Errors is not null)
                return result.Conflict ? Conflict(result.Errors) : Results.ValidationProblem(result.Errors);
            if (result.Job is null) return Results.Problem("Publishing job could not be created.", statusCode: 500);
            return result.Duplicate
                ? Results.Ok(result.Job.ToDto())
                : Results.Accepted($"/api/publishing/jobs/{result.Job.Id}", result.Job.ToDto());
        }).WithTags("Publishing");

        publishing.MapGet("/jobs", async (Guid? campaignId, PlatformKind? platform, PublishStatus? status,
            AppDbContext db, CancellationToken ct) =>
        {
            var query = db.PublishJobs.AsNoTracking().AsQueryable();
            if (campaignId.HasValue) query = query.Where(x => x.CampaignId == campaignId.Value);
            if (platform.HasValue) query = query.Where(x => x.Platform == platform.Value);
            if (status.HasValue) query = query.Where(x => x.Status == status.Value);
            return Results.Ok((await query.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct)).Select(x => x.ToDto()));
        });
        publishing.MapGet("/jobs/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.PublishJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job.ToDto());
        });
        publishing.MapPost("/jobs/{id:guid}/cancel", async (Guid id, HttpContext context,
            LocalPublishingRequestGuard guard, PublishJobService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var job = await service.CancelAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job.ToDto());
        });
        publishing.MapPost("/jobs/{id:guid}/confirm-instagram", async (Guid id,
            ConfirmInstagramPublishRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            PublishJobService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var result = await service.ConfirmInstagramAsync(id, request, ct);
            if (result.Errors is not null) return result.Conflict ? Conflict(result.Errors) : Results.ValidationProblem(result.Errors);
            return result.Job is null ? Results.NotFound() : Results.Accepted($"/api/publishing/jobs/{id}", result.Job.ToDto());
        });
        publishing.MapPost("/jobs/{id:guid}/complete-manual", async (Guid id,
            CompleteManualPublishRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            PublishJobService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var result = await service.CompleteManualAsync(id, request, ct);
            if (result.Errors is not null) return result.Conflict ? Conflict(result.Errors) : Results.ValidationProblem(result.Errors);
            return result.Job is null ? Results.NotFound() : Results.Ok(result.Job.ToDto());
        });
        publishing.MapGet("/jobs/{id:guid}/manual-package", async (Guid id, AppDbContext db,
            WorkspaceService workspace, CancellationToken ct) =>
        {
            var job = await db.PublishJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (job is null || job.Platform != PlatformKind.TikTok || string.IsNullOrWhiteSpace(job.ManualPackageRelativePath))
                return Results.NotFound();
            try
            {
                var root = workspace.ResolveRelative(Path.Combine("campaigns", job.CampaignId.ToString("N"), "output", "publish-packages"));
                var path = workspace.ResolveRelative(job.ManualPackageRelativePath);
                WorkspaceService.EnsureInside(root, path);
                if (PublishingMediaValidator.HasReparsePoint(root, path) || !File.Exists(path) ||
                    !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
                return Results.File(path, "application/zip", Path.GetFileName(path), enableRangeProcessing: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
        });
    }

    private static IResult Forbidden() => Results.Problem(
        title: "Publishing session expired",
        detail: "Refresh the local app and try the action again.",
        statusCode: StatusCodes.Status403Forbidden);

    private static IResult Conflict(Dictionary<string, string[]> errors) => Results.Json(new
    {
        title = "Publishing request conflict",
        errors
    }, statusCode: StatusCodes.Status409Conflict);

    private static IResult SafeProblem(string? message, int statusCode) => Results.Problem(
        title: "Publishing setup could not be completed",
        detail: PlatformConnectionService.CleanError(message) ?? "Review the platform setup and try again.",
        statusCode: statusCode);

    private static IResult OAuthPage(bool success, string message, int statusCode)
    {
        var color = success ? "#10b981" : "#f97316";
        var title = success ? "YouTube connected" : "YouTube connection not completed";
        var html = $"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{title}</title></head><body style="margin:0;background:#071a14;color:#f8fafc;font:16px system-ui;display:grid;min-height:100vh;place-items:center">
            <main style="max-width:560px;padding:32px;border:1px solid #24483b;border-radius:18px;background:#0d2a20">
            <div style="width:14px;height:14px;border-radius:50%;background:{color}"></div><h1>{title}</h1><p style="line-height:1.6;color:#b8d4c8">{message}</p>
            </main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }
}
