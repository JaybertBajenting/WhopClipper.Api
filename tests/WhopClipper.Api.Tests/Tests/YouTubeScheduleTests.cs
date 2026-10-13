using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class PublishSlotTests
{
    private static readonly YouTubeScheduleSettings Singapore = new(["23:00", "02:00", "05:00", "08:00"], "Singapore Standard Time");

    [Fact]
    public void SlotsAreTheFourDailyLocalTimesInOrder()
    {
        // 1:30 pm in Singapore (UTC+8).
        var after = new DateTimeOffset(2026, 9, 26, 5, 30, 0, TimeSpan.Zero);

        var slots = PublishSlots.After(after, Singapore).Take(5).ToList();

        Assert.Equal(new[]
        {
            new DateTimeOffset(2026, 9, 26, 15, 0, 0, TimeSpan.Zero), // 11 pm
            new DateTimeOffset(2026, 9, 26, 18, 0, 0, TimeSpan.Zero), // 2 am
            new DateTimeOffset(2026, 9, 26, 21, 0, 0, TimeSpan.Zero), // 5 am
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),  // 8 am
            new DateTimeOffset(2026, 9, 27, 15, 0, 0, TimeSpan.Zero), // next 11 pm
        }, slots);
    }

    [Fact]
    public void ANewClipTakesTheEarliestSlotNobodyHolds()
    {
        var after = new DateTimeOffset(2026, 9, 26, 5, 30, 0, TimeSpan.Zero);
        var taken = new HashSet<DateTimeOffset>
        {
            new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero), new(2026, 9, 26, 21, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 18, 0, 0, TimeSpan.Zero), PublishSlots.FirstFree(after, taken, Singapore));
    }

    [Fact]
    public void TakingAClipOffMovesEveryLaterClipUpOneSlot()
    {
        var eleven = new DateTimeOffset(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);
        var two = eleven.AddHours(3);
        var five = eleven.AddHours(6);
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var moves = PublishSlots.CloseGap(eleven, [(c, five), (b, two)], eleven.AddHours(-2));

        Assert.Equal(new[] { (b, eleven), (c, two) }, moves);
        Assert.Empty(PublishSlots.CloseGap(eleven, [(b, two)], eleven.AddMinutes(1)));
    }

    [Fact]
    public void QuotaWaitsForMidnightPacific()
    {
        var reset = YouTubeScheduleService.NextQuotaReset(new DateTimeOffset(2026, 9, 26, 5, 30, 0, TimeSpan.Zero));
        // 05:30 UTC is still 10:30 pm on the 25th in California (daylight time), so the next midnight
        // PDT is 07:00 UTC the same day.
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 7, 5, 0, TimeSpan.Zero), reset);
        Assert.True(YouTubeScheduleService.IsQuotaError(new PublishingApiException(
            "YouTube could not start the resumable upload (403): The request cannot be completed because you have exceeded your quota.",
            HttpStatusCode.Forbidden)));
        Assert.False(YouTubeScheduleService.IsQuotaError(new PublishingApiException("forbidden", HttpStatusCode.Forbidden)));
    }

    [Fact]
    public void TheChannelsOwnDailyLimitIsAWaitNotAFailure()
    {
        Assert.True(YouTubeScheduleService.IsChannelUploadLimit(new PublishingApiException(
            "YouTube could not start the resumable upload (400): The user has exceeded the number of videos they may upload.",
            HttpStatusCode.BadRequest)));
        Assert.False(YouTubeScheduleService.IsChannelUploadLimit(new PublishingApiException(
            "YouTube could not start the resumable upload (400): The request metadata specifies an invalid video title.",
            HttpStatusCode.BadRequest)));
    }
}

public sealed class PhoneNotifierTests
{
    [Fact]
    public void GoLiveNotificationCopiesTheShortsLinkAndOpensTheCampaign()
    {
        var job = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddHours(3);
        using var request = PhoneNotifier.BuildGoLive(new PhoneNotificationSettings(true, "https://ntfy.sh", "whopclipper-abc123def456"),
            job, "Gaethje collected belts like Infinity Stones \U0001F94A", "abcDEF12345", "Double Coverage Podcast Clips",
            "https://reachclipping.com/Lacy#resources", at, DateTimeOffset.UtcNow);

        Assert.Equal($"https://ntfy.sh/whopclipper-abc123def456/wc-{job:N}", request.RequestUri!.ToString());
        Assert.Equal(at.ToUnixTimeSeconds().ToString(), request.Headers.GetValues("At").Single());
        Assert.Equal("https://reachclipping.com/Lacy#resources", request.Headers.GetValues("Click").Single());
        using var actions = JsonDocument.Parse(request.Headers.GetValues("Actions").Single());
        var copy = actions.RootElement.EnumerateArray().First();
        Assert.Equal("copy", copy.GetProperty("action").GetString());
        Assert.Equal("https://youtube.com/shorts/abcDEF12345", copy.GetProperty("value").GetString());
        Assert.Contains(actions.RootElement.EnumerateArray(), x =>
            x.TryGetProperty("url", out var url) && url.GetString() == "https://reachclipping.com/Lacy#resources");
        Assert.All(request.Headers.SelectMany(x => x.Value), value => Assert.True(value.All(c => c < 128), value));
        Assert.Contains("\U0001F94A", request.Content!.ReadAsStringAsync().Result);
    }

    [Fact]
    public void ALateNotificationIsSentNowRatherThanScheduledInThePast()
    {
        using var request = PhoneNotifier.BuildGoLive(new PhoneNotificationSettings(true, "https://ntfy.sh", "whopclipper-abc123def456"),
            Guid.NewGuid(), "t", "abcDEF12345", "c", null, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
        Assert.False(request.Headers.Contains("At"));
        Assert.Equal("https://youtube.com/shorts/abcDEF12345", request.Headers.GetValues("Click").Single());
    }
}

public sealed class YouTubeScheduleServiceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase("schedule-" + Guid.NewGuid().ToString("N")).Options);
    private readonly RecordingHandler _http = new();
    private readonly Campaign _campaign = new() { Name = "Double Coverage Podcast Clips", WhopUrl = "https://reachclipping.com/Lacy#resources" };

    public YouTubeScheduleServiceTests()
    {
        _db.Campaigns.Add(_campaign);
        _db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = PlatformKind.YouTubeShorts, CredentialReference = "ref", AccountIdentifier = "@dc",
            GrantedScopesJson = JsonSerializer.Serialize(new[] { YouTubeOAuthService.UploadScope, YouTubeOAuthService.ManageScope }),
            LastValidatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private YouTubeScheduleService Service()
    {
        var factory = new SingleClientFactory(_http);
        var connections = new PlatformConnectionService(_db, new FakeVault(), []);
        return new YouTubeScheduleService(_db, new PublishingSettingsStore(_db), new PublishJobService(_db, null!, connections, null!),
            connections, new YouTubePublisher(factory, Options.Create(new PublishingApiOptions())),
            new PhoneNotifier(factory, NullLogger<PhoneNotifier>.Instance), NullLogger<YouTubeScheduleService>.Instance);
    }

    private PublishJob Seed(PublishStatus status, DateTimeOffset slot, string? videoId, bool notified = false)
    {
        var job = new PublishJob
        {
            CampaignId = _campaign.Id, Platform = PlatformKind.YouTubeShorts, Status = status, Title = "Clip",
            IdempotencyKey = Guid.NewGuid().ToString(), ScheduledPublishAt = slot, ExternalId = videoId,
            NotificationScheduledFor = notified ? slot : null,
        };
        _db.PublishJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    [Fact]
    public async Task TakingOffDeletesTheVideoAndMovesTheLaterClipsIntoTheFreedSlots()
    {
        var first = DateTimeOffset.UtcNow.AddHours(5).AddTicks(-(DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerMinute));
        var a = Seed(PublishStatus.Scheduled, first, "vidA", notified: true);
        var b = Seed(PublishStatus.Scheduled, first.AddHours(3), "vidB");
        var c = Seed(PublishStatus.Queued, first.AddHours(6), null);

        var (job, error) = await Service().UnscheduleAsync(a.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(PublishStatus.Removed, job!.Status);
        Assert.Equal(first, (await _db.PublishJobs.SingleAsync(x => x.Id == b.Id)).ScheduledPublishAt);
        Assert.Equal(first.AddHours(3), (await _db.PublishJobs.SingleAsync(x => x.Id == c.Id)).ScheduledPublishAt);
        Assert.Contains(_http.Calls, x => x == "DELETE /youtube/v3/videos?id=vidA");
        Assert.Contains(_http.Calls, x => x.StartsWith("DELETE /whopclipper-", StringComparison.Ordinal) && x.EndsWith($"/wc-{a.Id:N}", StringComparison.Ordinal));
        var update = Assert.Single(_http.Bodies, x => x.Contains("\"vidB\""));
        Assert.Contains(YouTubePublisher.FormatPublishAt(first), update);
        Assert.Contains("\"privacyStatus\":\"private\"", update);
        Assert.DoesNotContain(_http.Calls, x => x.Contains("vidC"));
    }

    [Fact]
    public async Task AClipThatCannotMoveStopsTheShiftSoTwoClipsNeverShareASlot()
    {
        var first = DateTimeOffset.UtcNow.AddHours(5);
        var a = Seed(PublishStatus.Scheduled, first, "vidA");
        var b = Seed(PublishStatus.Scheduled, first.AddHours(3), "vidB");
        var c = Seed(PublishStatus.Scheduled, first.AddHours(6), "vidC");
        _http.FailUpdatesFor = "vidB";

        await Service().UnscheduleAsync(a.Id, CancellationToken.None);

        Assert.Equal(first.AddHours(3), (await _db.PublishJobs.SingleAsync(x => x.Id == b.Id)).ScheduledPublishAt);
        Assert.Equal(first.AddHours(6), (await _db.PublishJobs.SingleAsync(x => x.Id == c.Id)).ScheduledPublishAt);
        Assert.DoesNotContain(_http.Bodies, x => x.Contains("\"vidC\""));
    }

    [Fact]
    public async Task SyncMarksPassedSlotsPublicAndSchedulesTheNextNotification()
    {
        await new PublishingSettingsStore(_db).SaveNotificationsAsync(
            new PhoneNotificationSettings(true, "https://ntfy.sh", "whopclipper-test-topic-1"), CancellationToken.None);
        var passed = Seed(PublishStatus.Scheduled, DateTimeOffset.UtcNow.AddMinutes(-3), "vidOld", notified: true);
        var soon = Seed(PublishStatus.Scheduled, DateTimeOffset.UtcNow.AddHours(2), "vidSoon");
        var far = Seed(PublishStatus.Scheduled, DateTimeOffset.UtcNow.AddDays(5), "vidFar");

        await Service().SyncAsync(CancellationToken.None);

        Assert.Equal(PublishStatus.Published, (await _db.PublishJobs.SingleAsync(x => x.Id == passed.Id)).Status);
        Assert.Equal(soon.ScheduledPublishAt, (await _db.PublishJobs.SingleAsync(x => x.Id == soon.Id)).NotificationScheduledFor);
        Assert.Null((await _db.PublishJobs.SingleAsync(x => x.Id == far.Id)).NotificationScheduledFor);
        Assert.Contains(_http.Calls, x => x == $"POST /whopclipper-test-topic-1/wc-{soon.Id:N}");
        Assert.DoesNotContain(_http.Calls, x => x.Contains(passed.Id.ToString("N")));
    }

    [Fact]
    public async Task ChangingACampaignsLinkReplacesTheNotificationsAlreadySet()
    {
        await new PublishingSettingsStore(_db).SaveNotificationsAsync(
            new PhoneNotificationSettings(true, "https://ntfy.sh", "whopclipper-test-topic-1"), CancellationToken.None);
        var set = Seed(PublishStatus.Scheduled, DateTimeOffset.UtcNow.AddHours(5), "vidSet", notified: true);
        var service = Service();

        _campaign.WhopUrl = "https://whop.com/new-campaign-page";
        _db.SaveChanges();
        await service.RefreshNotificationsAsync(_campaign.Id, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        // Sent again under the clip's own ID, which is how ntfy replaces the message it is holding.
        Assert.Contains(_http.Calls, x => x == $"POST /whopclipper-test-topic-1/wc-{set.Id:N}");
        Assert.Equal(["https://whop.com/new-campaign-page"], _http.Clicks);
        Assert.Equal(set.ScheduledPublishAt, (await _db.PublishJobs.SingleAsync(x => x.Id == set.Id)).NotificationScheduledFor);
    }

    [Fact]
    public async Task ReconnectingSendsWaitingClipsUpStraightAway()
    {
        var waiting = Seed(PublishStatus.Queued, DateTimeOffset.UtcNow.AddHours(5), null);
        waiting.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(15);
        waiting.Error = YouTubePublisher.SignedOut;
        var uploaded = Seed(PublishStatus.Scheduled, DateTimeOffset.UtcNow.AddHours(8), "vidUp");
        uploaded.Error = "kept";
        _db.SaveChanges();

        await Service().RetryWaitingAsync(CancellationToken.None);

        var retried = await _db.PublishJobs.SingleAsync(x => x.Id == waiting.Id);
        Assert.Null(retried.NextAttemptAt);
        Assert.Null(retried.Error);
        Assert.Equal("kept", (await _db.PublishJobs.SingleAsync(x => x.Id == uploaded.Id)).Error);
    }

    private sealed class FakeVault : IPublishingCredentialVault
    {
        public string Store(PlatformKind platform, IReadOnlyDictionary<string, string> credentials) => "ref";
        public Dictionary<string, string> Read(string reference) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["clientId"] = "client", ["accountId"] = "@dc", ["accessToken"] = "token", ["refreshToken"] = "refresh",
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            ["grantedScopes"] = $"{YouTubeOAuthService.UploadScope} {YouTubeOAuthService.ManageScope}",
        };
        public void Delete(string reference) { }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>Plays YouTube and ntfy: records every call and answers the way each service does.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public List<string> Bodies { get; } = [];
        /// <summary>Where each notification opens when it is tapped.</summary>
        public List<string> Clicks { get; } = [];
        public string? FailUpdatesFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Headers.TryGetValues("Click", out var click)) Clicks.AddRange(click);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 0) Bodies.Add(body);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/videos", StringComparison.Ordinal))
                return Json("""{"items":[{"status":{"privacyStatus":"private","publishAt":"2030-01-01T00:00:00Z","license":"youtube","embeddable":true,"selfDeclaredMadeForKids":false,"uploadStatus":"processed"}}]}""");
            if (request.Method == HttpMethod.Put && FailUpdatesFor is { } failing && body.Contains($"\"{failing}\""))
                return Json("""{"error":{"message":"backend error"}}""", HttpStatusCode.InternalServerError);
            return request.Method == HttpMethod.Delete && request.RequestUri.Host.Contains("googleapis")
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json("{}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
