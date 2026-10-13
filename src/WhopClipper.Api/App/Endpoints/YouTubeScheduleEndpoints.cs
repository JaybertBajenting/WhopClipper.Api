using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

/// <summary>The YouTube posting schedule and the phone notifications that announce each clip going public.</summary>
public static class YouTubeScheduleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var publishing = api.MapGroup("/publishing").WithTags("Publishing");

        publishing.MapGet("/youtube-schedule", async (Guid? pageId, YouTubeScheduleService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(pageId, null, ct)));

        // The schedule of the page this campaign posts to, with the campaign's own clips on it.
        api.MapGet("/campaigns/{campaignId:guid}/publishing/youtube-schedule", async (Guid campaignId,
            YouTubeScheduleService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(null, campaignId, ct))).WithTags("Publishing");

        api.MapPut("/campaigns/{campaignId:guid}/publishing/youtube-page", async (Guid campaignId,
            SetCampaignYouTubePageRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            PlatformConnectionService connections, YouTubeScheduleService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var result = await connections.SetCampaignYouTubePageAsync(campaignId, request.PageId, ct);
            if (result.Error is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["pageId"] = [result.Error] });
            return result.Found ? Results.Ok(await service.GetAsync(null, campaignId, ct)) : Results.NotFound();
        }).WithTags("Publishing");

        publishing.MapPut("/youtube-schedule/settings", async (Guid? pageId, YouTubeScheduleSettingsDto request, HttpContext context,
            LocalPublishingRequestGuard guard, PublishingSettingsStore store, YouTubeScheduleService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var slots = request.Slots.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (slots.Count is 0 or > 12 || PublishSlots.Parse(slots).Count != slots.Distinct().Count())
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slots"] = ["Give 1 to 12 distinct times as HH:mm, e.g. 23:00."] });
            // Saved against the page the screen is showing, which is the first page when none is named.
            var shown = (await service.GetAsync(pageId, null, ct)).PageId;
            await store.SaveScheduleAsync(shown, new YouTubeScheduleSettings(slots, PublishSlots.Zone(request.TimeZoneId).Id), ct);
            return Results.Ok(await service.GetAsync(shown, null, ct));
        });

        api.MapPost("/campaigns/{campaignId:guid}/publishing/youtube-schedule", async (Guid campaignId,
            ScheduleYouTubeRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            YouTubeScheduleService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var (job, errors) = await service.ScheduleAsync(campaignId, request, ct);
            if (errors is { Count: > 0 }) return Results.ValidationProblem(errors);
            return job is null ? Results.NotFound() : Results.Accepted($"/api/publishing/jobs/{job.Id}", job.ToDto());
        }).WithTags("Publishing");

        publishing.MapPost("/jobs/{id:guid}/unschedule", async (Guid id, UnscheduleYouTubeRequest request,
            HttpContext context, LocalPublishingRequestGuard guard, YouTubeScheduleService service, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            if (!request.Confirmed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmed"] = ["Confirm taking this clip off YouTube."] });
            var (job, error) = await service.UnscheduleAsync(id, ct);
            if (error is not null)
                return Results.Problem(title: "Could not take the clip off", detail: PlatformConnectionService.CleanError(error), statusCode: 409);
            return job is null ? Results.NotFound() : Results.Ok(job.ToDto());
        });

        publishing.MapGet("/notifications", async (PublishingSettingsStore store, CancellationToken ct) =>
            Results.Ok(YouTubeScheduleService.ToDto(await store.GetNotificationsAsync(ct))));

        publishing.MapPut("/notifications", async (UpdatePhoneNotificationsRequest request, HttpContext context,
            LocalPublishingRequestGuard guard, PublishingSettingsStore store, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var current = await store.GetNotificationsAsync(ct);
            var server = string.IsNullOrWhiteSpace(request.Server) ? current.Server : request.Server.Trim().TrimEnd('/');
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["server"] = ["The ntfy server must be an https address."] });
            var topic = string.IsNullOrWhiteSpace(request.Topic) ? current.Topic : request.Topic.Trim();
            if (topic == "new") topic = PublishingSettingsStore.NewTopic();
            if (topic.Length is < 12 or > 64 || !topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["topic"] = ["Use 12-64 letters, digits, dashes or underscores. Anyone who knows the topic can read it."] });
            var saved = new PhoneNotificationSettings(request.Enabled, server, topic);
            await store.SaveNotificationsAsync(saved, ct);
            return Results.Ok(YouTubeScheduleService.ToDto(saved));
        });

        publishing.MapPost("/notifications/test", async (HttpContext context, LocalPublishingRequestGuard guard,
            PublishingSettingsStore store, PhoneNotifier notifier, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            try
            {
                await notifier.SendTestAsync(await store.GetNotificationsAsync(ct), ct);
                return Results.Ok(new { sent = true });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return Results.Problem(title: "The test notification did not send", detail: ex.Message, statusCode: 502);
            }
        });
    }

    private static IResult Forbidden() => Results.Problem(
        title: "Publishing session expired",
        detail: "Refresh the local app and try the action again.",
        statusCode: StatusCodes.Status403Forbidden);
}
