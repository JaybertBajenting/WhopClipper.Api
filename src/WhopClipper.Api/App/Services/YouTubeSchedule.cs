using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <param name="Slots">Local go-public times, "HH:mm".</param>
public sealed record YouTubeScheduleSettings(IReadOnlyList<string> Slots, string TimeZoneId)
{
    /// <summary>Matthew's four daily slots, 11 pm / 2 am / 5 am / 8 am in this machine's time zone.</summary>
    public static YouTubeScheduleSettings Default => new(["23:00", "02:00", "05:00", "08:00"], TimeZoneInfo.Local.Id);
}

/// <param name="Topic">The ntfy topic the phone subscribes to. Anyone who knows it can read the
/// notifications, so it is long and random rather than a readable name.</param>
public sealed record PhoneNotificationSettings(bool Enabled, string Server, string Topic);

/// <summary>Daily go-public slots. Pure time arithmetic, kept apart from the database for testing.</summary>
public static class PublishSlots
{
    public static IReadOnlyList<TimeSpan> Parse(IEnumerable<string> slots)
    {
        var parsed = slots.Select(x => TimeSpan.TryParseExact(x.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time) ? time : (TimeSpan?)null)
            .Where(x => x is { } time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)).Select(x => x!.Value)
            .Distinct().Order().ToList();
        return parsed.Count > 0 ? parsed : [TimeSpan.FromHours(23)];
    }

    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    /// <summary>Every slot strictly after <paramref name="afterUtc"/>, earliest first.</summary>
    public static IEnumerable<DateTimeOffset> After(DateTimeOffset afterUtc, YouTubeScheduleSettings settings, int days = 400)
    {
        var zone = Zone(settings.TimeZoneId);
        var times = Parse(settings.Slots);
        var localDate = TimeZoneInfo.ConvertTime(afterUtc, zone).Date;
        for (var day = -1; day <= days; day++)
        {
            var date = localDate.AddDays(day);
            foreach (var time in times)
            {
                var local = DateTime.SpecifyKind(date + time, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;
                var slot = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
                if (slot > afterUtc) yield return slot;
            }
        }
    }

    public static DateTimeOffset FirstFree(DateTimeOffset afterUtc, IReadOnlySet<DateTimeOffset> taken, YouTubeScheduleSettings settings) =>
        After(afterUtc, settings).First(slot => !taken.Contains(slot));

    /// <summary>
    /// Taking a clip off moves every later clip up one slot, so the freed time is not left empty: the
    /// first later clip takes the freed slot, the next takes the first one's, and so on. A freed slot too
    /// close to use changes nothing.
    /// </summary>
    public static IReadOnlyList<(Guid Id, DateTimeOffset Slot)> CloseGap(DateTimeOffset freed,
        IEnumerable<(Guid Id, DateTimeOffset Slot)> later, DateTimeOffset earliestUsable)
    {
        if (freed < earliestUsable) return [];
        var ordered = later.Where(x => x.Slot > freed).OrderBy(x => x.Slot).ToList();
        var moves = new List<(Guid Id, DateTimeOffset Slot)>(ordered.Count);
        var target = freed;
        foreach (var (id, slot) in ordered)
        {
            moves.Add((id, target));
            target = slot;
        }
        return moves;
    }
}

public sealed class PublishingSettingsStore(AppDbContext db)
{
    private const string ScheduleKey = "youtube.schedule";
    private const string NotificationsKey = "notifications.ntfy";

    private static string ScheduleKeyFor(Guid? pageId) => pageId is { } id ? $"{ScheduleKey}:{id:N}" : ScheduleKey;

    /// <summary>A page's own daily times. Until it saves some, it uses the shared ones every page starts with.</summary>
    public async Task<YouTubeScheduleSettings> GetScheduleAsync(Guid? pageId, CancellationToken cancellationToken)
    {
        var own = ScheduleKeyFor(pageId);
        var rows = await db.ApplicationSettings.AsNoTracking().Where(x => x.Key == own || x.Key == ScheduleKey).ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault(x => x.Key == own) ?? rows.FirstOrDefault();
        return row is null ? YouTubeScheduleSettings.Default
            : JsonDefaults.Parse<YouTubeScheduleSettings?>(row.Value, null) ?? YouTubeScheduleSettings.Default;
    }

    public Task SaveScheduleAsync(Guid? pageId, YouTubeScheduleSettings settings, CancellationToken cancellationToken) =>
        SaveAsync(ScheduleKeyFor(pageId), settings, cancellationToken);

    /// <summary>First use creates the topic, so the phone can subscribe before anything is scheduled.</summary>
    public async Task<PhoneNotificationSettings> GetNotificationsAsync(CancellationToken cancellationToken)
    {
        var row = await db.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == NotificationsKey, cancellationToken);
        if (row is not null && JsonDefaults.Parse<PhoneNotificationSettings?>(row.Value, null) is { } saved) return saved;
        var created = new PhoneNotificationSettings(false, "https://ntfy.sh", NewTopic());
        await SaveNotificationsAsync(created, cancellationToken);
        return created;
    }

    public Task SaveNotificationsAsync(PhoneNotificationSettings settings, CancellationToken cancellationToken) =>
        SaveAsync(NotificationsKey, settings, cancellationToken);

    public static string NewTopic() =>
        "whopclipper-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    private async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        var row = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (row is null) db.ApplicationSettings.Add(row = new ApplicationSetting { Key = key });
        row.Value = JsonSerializer.Serialize(value, JsonDefaults.Serializer);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Phone notifications through ntfy (the free ntfy app, subscribed to one topic). The message is
/// scheduled on the ntfy server for the go-public time, so it arrives even when this PC is off, and it
/// is cancelled or moved by its sequence ID when a clip is taken off or shifts to another slot.
/// </summary>
public sealed class PhoneNotifier(IHttpClientFactory clients, ILogger<PhoneNotifier> logger)
{
    /// <summary>ntfy keeps a scheduled message for at most three days.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(71);

    public static string SequenceId(Guid jobId) => $"wc-{jobId:N}";

    public static string ShortsUrl(string videoId) => $"https://youtube.com/shorts/{Uri.EscapeDataString(videoId)}";

    public static HttpRequestMessage BuildGoLive(PhoneNotificationSettings settings, Guid jobId, string clipTitle,
        string videoId, string campaignName, string? campaignUrl, DateTimeOffset at, DateTimeOffset now)
    {
        var url = ShortsUrl(videoId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Server.TrimEnd('/')}/{settings.Topic}/{SequenceId(jobId)}")
        {
            // The body is UTF-8, so the clip's own title keeps its emoji; headers must stay ASCII.
            Content = new StringContent($"{clipTitle}\n{url}\nPaste this link into {campaignName}.", Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Title", "Your clip is live on YouTube");
        request.Headers.TryAddWithoutValidation("Tags", "tada");
        if (at > now.AddSeconds(30))
            request.Headers.TryAddWithoutValidation("At", at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var actions = new List<object> { new { action = "copy", label = "Copy YouTube link", value = url } };
        if (IsHttpUrl(campaignUrl))
        {
            actions.Add(new { action = "view", label = "Open campaign", url = campaignUrl });
            request.Headers.TryAddWithoutValidation("Click", campaignUrl);
        }
        else request.Headers.TryAddWithoutValidation("Click", url);
        actions.Add(new { action = "view", label = "Watch", url });
        request.Headers.TryAddWithoutValidation("Actions", JsonSerializer.Serialize(actions));
        return request;
    }

    public async Task ScheduleGoLiveAsync(PhoneNotificationSettings settings, Guid jobId, string clipTitle, string videoId,
        string campaignName, string? campaignUrl, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var request = BuildGoLive(settings, jobId, clipTitle, videoId, campaignName, campaignUrl, at, DateTimeOffset.UtcNow);
        using var response = await clients.CreateClient("PublishingExternal").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ntfy refused the notification ({(int)response.StatusCode}).");
    }

    public async Task CancelAsync(PhoneNotificationSettings settings, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await clients.CreateClient("PublishingExternal").DeleteAsync(
                $"{settings.Server.TrimEnd('/')}/{settings.Topic}/{SequenceId(jobId)}", cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning("ntfy could not cancel notification {Job} ({Status}).", jobId, (int)response.StatusCode);
        }
        catch (HttpRequestException ex) { logger.LogWarning(ex, "ntfy could not cancel notification {Job}.", jobId); }
    }

    public async Task SendTestAsync(PhoneNotificationSettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Server.TrimEnd('/')}/{settings.Topic}")
        {
            Content = new StringContent("Test from Whop Clipper. When a scheduled clip goes public you get its link here.",
                Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Title", "Whop Clipper notifications work");
        request.Headers.TryAddWithoutValidation("Tags", "white_check_mark");
        using var response = await clients.CreateClient("PublishingExternal").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ntfy refused the test ({(int)response.StatusCode}).");
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

/// <summary>
/// The YouTube posting schedule: one button to put a clip into the next free daily slot, one to take it
/// off. A clip uploads as soon as it is scheduled, private with a publishAt, so YouTube makes it public
/// at the slot by itself and this PC is only needed for the upload.
/// </summary>
public sealed class YouTubeScheduleService(AppDbContext db, PublishingSettingsStore settingsStore, PublishJobService jobs,
    PlatformConnectionService connections, YouTubePublisher youtube, PhoneNotifier notifier,
    ILogger<YouTubeScheduleService> logger)
{
    /// <summary>A slot must be at least this far off to upload into, or to move a clip into.</summary>
    public static readonly TimeSpan MinimumLead = TimeSpan.FromMinutes(20);

    // Two clicks at once must not both claim the same slot.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>States that hold their slot: anything uploaded, uploading, or waiting to upload.</summary>
    private static readonly PublishStatus[] Holding =
        [PublishStatus.Queued, PublishStatus.Validating, PublishStatus.Uploading, PublishStatus.Processing,
         PublishStatus.Scheduled, PublishStatus.NeedsAttention];

    /// <summary>How long an upload waits before trying again once the channel's own daily limit is reached.</summary>
    public static readonly TimeSpan ChannelLimitWait = TimeSpan.FromHours(3);

    /// <summary>How often a waiting upload checks whether YouTube has been reconnected. Reconnecting also retries at once.</summary>
    public static readonly TimeSpan ReconnectWait = TimeSpan.FromMinutes(15);

    /// <summary>The Google project's daily allowance (100 uploads by default) is used up.</summary>
    public static bool IsQuotaError(PublishingApiException exception) =>
        exception.StatusCode == HttpStatusCode.Forbidden && exception.Message.Contains("quota", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The channel has uploaded as many videos as YouTube lets it in a day (uploadLimitExceeded). The
    /// limit differs per channel and frees up over 24 hours rather than at a fixed time.
    /// </summary>
    public static bool IsChannelUploadLimit(PublishingApiException exception) =>
        exception.StatusCode == HttpStatusCode.BadRequest &&
        exception.Message.Contains("exceeded the number of videos", StringComparison.OrdinalIgnoreCase);

    /// <summary>YouTube's API quota resets at midnight Pacific time.</summary>
    public static DateTimeOffset NextQuotaReset(DateTimeOffset now)
    {
        var pacific = PublishSlots.Zone("Pacific Standard Time");
        var local = TimeZoneInfo.ConvertTime(now, pacific);
        var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, pacific.GetUtcOffset(midnight)).ToUniversalTime().AddMinutes(5);
    }

    /// <summary>YouTube was just reconnected: clips that were waiting upload now instead of at their next check.</summary>
    public async Task RetryWaitingAsync(CancellationToken cancellationToken)
    {
        var waiting = await db.PublishJobs
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && x.Status == PublishStatus.Queued && x.ScheduledPublishAt != null)
            .ToListAsync(cancellationToken);
        foreach (var job in waiting)
        {
            job.NextAttemptAt = null;
            job.Error = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A campaign's link or name changed, and the notifications already handed to ntfy carry the old one.
    /// Clearing their mark makes the next sync send each again under the same ID, which replaces the one
    /// ntfy is holding.
    /// </summary>
    public async Task RefreshNotificationsAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var held = await db.PublishJobs
            .Where(x => x.CampaignId == campaignId && x.Platform == PlatformKind.YouTubeShorts
                && x.Status == PublishStatus.Scheduled && x.NotificationScheduledFor != null)
            .ToListAsync(cancellationToken);
        foreach (var job in held) job.NotificationScheduledFor = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Each page has its own queue: a slot taken on one page is still free on another.</summary>
    public async Task<DateTimeOffset> NextFreeSlotAsync(DateTimeOffset after, Guid? except, Guid? pageId, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetScheduleAsync(pageId, cancellationToken);
        var taken = await db.PublishJobs.AsNoTracking()
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && x.ConnectionId == pageId && x.ScheduledPublishAt != null
                && Holding.Contains(x.Status) && x.Id != except)
            .Select(x => x.ScheduledPublishAt!.Value).ToListAsync(cancellationToken);
        return PublishSlots.FirstFree(after, taken.Select(x => x.ToUniversalTime()).ToHashSet(), settings);
    }

    /// <summary>
    /// One page's schedule. Asked for a campaign, it is the page that campaign posts to, plus that
    /// campaign's own clips wherever they were scheduled; otherwise the named page, or the first.
    /// </summary>
    public async Task<YouTubeScheduleDto> GetAsync(Guid? pageId, Guid? campaignId, CancellationToken cancellationToken)
    {
        var all = (await connections.GetAllAsync(cancellationToken))
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && x.Id is not null).ToList();
        var mustChoose = false;
        if (campaignId is { } campaign) (pageId, mustChoose) = await connections.ResolveYouTubePageAsync(campaign, cancellationToken);
        else pageId = all.FirstOrDefault(x => x.Id == pageId)?.Id ?? all.FirstOrDefault()?.Id;

        var settings = await settingsStore.GetScheduleAsync(pageId, cancellationToken);
        var notifications = await settingsStore.GetNotificationsAsync(cancellationToken);
        // With no page there is nothing to read, and GetAsync(null) would hand back the first page.
        var connection = all.FirstOrDefault(x => x.Id == pageId) ?? connections.ToDto(PlatformKind.YouTubeShorts, null);
        var connected = !mustChoose && connection.CanPublish;
        var canManage = !mustChoose && YouTubeOAuthService.CanManageSchedule(connection.GrantedScopes);
        var since = DateTimeOffset.UtcNow.AddDays(-2);
        // A page's schedule lists what is waiting and what went up in the last two days. A campaign's own clips
        // stay on it once they are public, however long ago: its clip list goes by this to mark a clip as posted,
        // and after two days a public clip looked unposted and was offered for scheduling again.
        var items = await db.PublishJobs.AsNoTracking()
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && x.ScheduledPublishAt != null
                && (Holding.Contains(x.Status) || x.ScheduledPublishAt > since
                    || (campaignId != null && x.CampaignId == campaignId && x.Status == PublishStatus.Published))
                && ((pageId != null && x.ConnectionId == pageId) || (campaignId != null && x.CampaignId == campaignId)))
            .OrderBy(x => x.ScheduledPublishAt).ToListAsync(cancellationToken);
        var message = mustChoose ? PlatformConnectionService.ChoosePage
            : !connection.CanPublish ? connection.CapabilityMessage
            : !canManage ? "Reconnect YouTube once: scheduling needs permission to move and delete videos, not just upload them."
            : "Ready. Each clip goes into the next free slot and YouTube makes it public on time, even if this PC is off.";
        var zone = PublishSlots.Zone(settings.TimeZoneId);
        return new(new(settings.Slots, zone.Id, zone.DisplayName),
            ToDto(notifications), connected, canManage,
            pageId is null ? null : PlatformConnectionService.PageLabel(connection.AccountIdentifier, connection.ChannelTitle), message,
            await NextFreeSlotAsync(DateTimeOffset.UtcNow + MinimumLead, null, pageId, cancellationToken),
            items.Select(x => x.ToDto()).ToList(), pageId,
            all.Select(x => new YouTubePageDto(x.Id!.Value, PlatformConnectionService.PageLabel(x.AccountIdentifier, x.ChannelTitle),
                x.CanPublish && YouTubeOAuthService.CanManageSchedule(x.GrantedScopes))).ToList());
    }

    public static PhoneNotificationSettingsDto ToDto(PhoneNotificationSettings settings) =>
        new(settings.Enabled, settings.Server, settings.Topic, $"{settings.Server.TrimEnd('/')}/{settings.Topic}");

    public async Task<(PublishJob? Job, Dictionary<string, string[]>? Errors)> ScheduleAsync(Guid campaignId,
        ScheduleYouTubeRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
            return (null, new() { ["confirmed"] = ["Review the clip, title and slot, then confirm."] });
        var (pageId, mustChoose) = await connections.ResolveYouTubePageAsync(campaignId, cancellationToken);
        if (mustChoose) return (null, new() { ["platform"] = [PlatformConnectionService.ChoosePage] });
        var connection = await connections.GetAsync(PlatformKind.YouTubeShorts, pageId, cancellationToken);
        if (!connection.CanPublish) return (null, new() { ["platform"] = [connection.CapabilityMessage] });
        if (!YouTubeOAuthService.CanManageSchedule(connection.GrantedScopes))
            return (null, new() { ["platform"] = ["Reconnect YouTube once: scheduling needs permission to move and delete videos, not just upload them."] });
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var slot = await NextFreeSlotAsync(DateTimeOffset.UtcNow + MinimumLead, null, pageId, cancellationToken);
            var created = await jobs.CreateAsync(campaignId, new CreatePublishJobRequest(PlatformKind.YouTubeShorts,
                    request.OutputRelativePath, Trim(request.Title, 100), request.Caption ?? string.Empty, "private", true,
                    Confirmed: true, IdempotencyKey: request.IdempotencyKey),
                cancellationToken, new ScheduledUpload(slot, request.EditPlanId));
            if (created.CampaignNotFound) return (null, new() { ["campaign"] = ["Campaign not found."] });
            return (created.Job, created.Errors);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Takes a clip off: cancelled if it has not uploaded yet, deleted from YouTube if it has. Later
    /// clips then move up a slot each so the freed time is used, and their notifications move with them.
    /// </summary>
    public async Task<(PublishJob? Job, string? Error)> UnscheduleAsync(Guid id, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var job = await db.PublishJobs.Include(x => x.Campaign).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (job is null) return (null, null);
            if (job.Platform != PlatformKind.YouTubeShorts || job.ScheduledPublishAt is null)
                return (null, "Only a scheduled YouTube clip can be taken off the schedule.");
            if (job.Status is PublishStatus.Removed or PublishStatus.Cancelled or PublishStatus.Failed) return (job, null);
            if (job.Status is PublishStatus.Validating or PublishStatus.Uploading or PublishStatus.Processing)
                return (null, "This clip is uploading right now. Take it off in a minute, once the upload finishes.");

            var credentials = await connections.GetCredentialsAsync(PlatformKind.YouTubeShorts, job.ConnectionId, cancellationToken)
                ?? new(StringComparer.OrdinalIgnoreCase);
            var freed = job.ScheduledPublishAt.Value;
            var wasHolding = Holding.Contains(job.Status);
            if (!string.IsNullOrWhiteSpace(job.ExternalId) && !job.DryRun)
            {
                try { await youtube.DeleteVideoAsync(job.ExternalId, credentials, cancellationToken); }
                catch (PublishingApiException ex) { return (null, ex.Message); }
            }
            job.Status = PublishStatus.Removed;
            job.Error = null;
            job.NextAttemptAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            var notifications = await settingsStore.GetNotificationsAsync(cancellationToken);
            if (job.NotificationScheduledFor is not null)
            {
                await notifier.CancelAsync(notifications, job.Id, cancellationToken);
                job.NotificationScheduledFor = null;
            }
            await db.SaveChangesAsync(cancellationToken);

            if (wasHolding) await CloseGapAsync(freed, job.ConnectionId, credentials, notifications, cancellationToken);
            return (job, null);
        }
        finally { Gate.Release(); }
    }

    private async Task CloseGapAsync(DateTimeOffset freed, Guid? pageId, Dictionary<string, string> credentials,
        PhoneNotificationSettings notifications, CancellationToken cancellationToken)
    {
        var later = await db.PublishJobs.Include(x => x.Campaign)
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && x.ConnectionId == pageId && x.ScheduledPublishAt > freed
                && Holding.Contains(x.Status))
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var moves = PublishSlots.CloseGap(freed, later.Select(x => (x.Id, x.ScheduledPublishAt!.Value)), now + MinimumLead);
        foreach (var (jobId, slot) in moves)
        {
            var job = later.Single(x => x.Id == jobId);
            // Stop at the first clip that cannot move: moving the ones after it would put two clips in
            // one slot.
            if (job.Status == PublishStatus.Scheduled && !string.IsNullOrWhiteSpace(job.ExternalId))
            {
                try
                {
                    if (!job.DryRun) await youtube.RescheduleAsync(job.ExternalId, slot, credentials, cancellationToken);
                }
                catch (PublishingApiException ex)
                {
                    job.Error = $"Could not move up a slot: {ex.Message}";
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogWarning("Could not move scheduled clip {Job} to {Slot}: {Error}", job.Id, slot, ex.Message);
                    break;
                }
            }
            else if (job.Status != PublishStatus.Queued) break;
            job.ScheduledPublishAt = slot;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await ScheduleNotificationAsync(job, notifications, cancellationToken);
        }
    }

    /// <summary>
    /// Runs every minute. Marks clips whose slot has passed as public, and keeps each uploaded clip's
    /// phone notification scheduled for its current slot (ntfy holds a message for three days at most,
    /// so a clip further out is picked up on a later pass).
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var notifications = await settingsStore.GetNotificationsAsync(cancellationToken);
        var scheduled = await db.PublishJobs.Include(x => x.Campaign)
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && x.Status == PublishStatus.Scheduled && x.ScheduledPublishAt != null)
            .ToListAsync(cancellationToken);
        foreach (var job in scheduled)
        {
            var slot = job.ScheduledPublishAt!.Value;
            if (slot <= now)
            {
                job.Status = PublishStatus.Published;
                job.PublishedAt = slot;
                job.UpdatedAt = now;
                // Never scheduled on ntfy (turned on late, or the PC was off all three days before):
                // still tell the phone, unless the news is stale.
                if (job.NotificationScheduledFor is null && now - slot < TimeSpan.FromHours(6))
                    await ScheduleNotificationAsync(job, notifications, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            if (job.NotificationScheduledFor != slot && slot - now <= PhoneNotifier.MaximumDelay)
                await ScheduleNotificationAsync(job, notifications, cancellationToken);
        }
    }

    private async Task ScheduleNotificationAsync(PublishJob job, PhoneNotificationSettings notifications,
        CancellationToken cancellationToken)
    {
        if (!notifications.Enabled || string.IsNullOrWhiteSpace(job.ExternalId) || job.DryRun || job.ScheduledPublishAt is not { } slot)
            return;
        try
        {
            await notifier.ScheduleGoLiveAsync(notifications, job.Id, job.Title, job.ExternalId, job.Campaign.Name,
                job.Campaign.WhopUrl, slot, cancellationToken);
            job.NotificationScheduledFor = slot;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning("Could not schedule the phone notification for {Job}: {Error}", job.Id, ex.Message);
        }
    }

    private static string Trim(string value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max].TrimEnd();
    }
}

public sealed class YouTubeScheduleWorker(IServiceScopeFactory scopeFactory, ILogger<YouTubeScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<YouTubeScheduleService>().SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning("YouTube schedule sync failed with {ErrorType}.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
