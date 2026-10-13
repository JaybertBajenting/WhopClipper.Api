using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>Several YouTube pages side by side: one per campaign, each with its own sign-in, queue and daily times.</summary>
public sealed class YouTubePagesTests : IDisposable
{
    private const string Callback = "http://127.0.0.1:5000/api/publishing/oauth/youtube/callback";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase("pages-" + Guid.NewGuid().ToString("N")).Options);
    private readonly MemoryVault _vault = new();
    private readonly GoogleHandler _google = new();
    private readonly Campaign _tjr = new() { Name = "TJR Trades Clips" };
    private readonly Campaign _podcast = new() { Name = "Double Coverage Podcast Clips" };

    public YouTubePagesTests()
    {
        _db.Campaigns.AddRange(_tjr, _podcast);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private YouTubePublisher Publisher() => new(new OneClient(_google), Options.Create(new PublishingApiOptions()));
    private PlatformConnectionService Connections() => new(_db, _vault, [Publisher()]);

    private YouTubeScheduleService Schedule()
    {
        var connections = Connections();
        return new YouTubeScheduleService(_db, new PublishingSettingsStore(_db), new PublishJobService(_db, null!, connections, null!),
            connections, Publisher(), new PhoneNotifier(new OneClient(_google), NullLogger<PhoneNotifier>.Instance),
            NullLogger<YouTubeScheduleService>.Instance);
    }

    /// <summary>A page that is signed in with both permissions and validated.</summary>
    private Guid Page(string handle, int minutesAgo = 0)
    {
        var row = new PlatformConnection
        {
            Platform = PlatformKind.YouTubeShorts, AccountIdentifier = handle,
            CredentialReference = _vault.Store(PlatformKind.YouTubeShorts, new Dictionary<string, string>
            {
                ["clientId"] = "client", ["clientSecret"] = "secret", ["accountId"] = handle,
                ["accessToken"] = $"access-{handle}", ["refreshToken"] = $"refresh-{handle}",
                ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            }),
            GrantedScopesJson = JsonSerializer.Serialize(new[] { YouTubeOAuthService.UploadScope, YouTubeOAuthService.ManageScope }),
            LastValidatedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        };
        _db.PlatformConnections.Add(row);
        _db.SaveChanges();
        return row.Id;
    }

    private PublishJob Clip(Guid page, DateTimeOffset slot, PublishStatus status = PublishStatus.Scheduled)
    {
        var job = new PublishJob
        {
            CampaignId = _tjr.Id, Platform = PlatformKind.YouTubeShorts, Status = status, Title = "Clip",
            IdempotencyKey = Guid.NewGuid().ToString(), ScheduledPublishAt = slot, ConnectionId = page, ExternalId = "vid",
        };
        _db.PublishJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    [Fact]
    public async Task ACampaignUsesTheOnlyPageAndHasToChooseOnceThereAreTwo()
    {
        var tjrDaily = Page("@TJRDaily", minutesAgo: 10);
        Assert.Equal(((Guid?)tjrDaily, false), await Connections().ResolveYouTubePageAsync(_podcast.Id, None));

        var podcastPage = Page("@DoubleCoverageClips");
        Assert.Equal(((Guid?)null, true), await Connections().ResolveYouTubePageAsync(_podcast.Id, None));
        var undecided = await Schedule().GetAsync(null, _podcast.Id, None);
        Assert.False(undecided.Connected);
        Assert.Null(undecided.PageId);
        Assert.Equal(PlatformConnectionService.ChoosePage, undecided.Message);
        Assert.Equal(["@TJRDaily", "@DoubleCoverageClips"], undecided.Pages.Select(x => x.Label));

        Assert.Equal((true, (string?)null), await Connections().SetCampaignYouTubePageAsync(_podcast.Id, podcastPage, None));
        var chosen = await Schedule().GetAsync(null, _podcast.Id, None);
        Assert.True(chosen.Connected);
        Assert.True(chosen.CanManage);
        Assert.Equal(podcastPage, chosen.PageId);
        Assert.Equal("@DoubleCoverageClips", chosen.Channel);
        // The other campaign is not moved by this one's choice: it still has to make its own.
        Assert.Equal(((Guid?)null, true), await Connections().ResolveYouTubePageAsync(_tjr.Id, None));
    }

    [Fact]
    public async Task EachPageHasItsOwnQueueAndItsOwnDailyTimes()
    {
        var a = Page("@A", minutesAgo: 10);
        var b = Page("@B");
        var service = Schedule();
        var after = DateTimeOffset.UtcNow.AddMinutes(30);
        var first = await service.NextFreeSlotAsync(after, null, a, None);
        Clip(a, first);

        Assert.NotEqual(first, await service.NextFreeSlotAsync(after, null, a, None));
        Assert.Equal(first, await service.NextFreeSlotAsync(after, null, b, None));
        Assert.Single((await service.GetAsync(a, null, None)).Items);
        Assert.Empty((await service.GetAsync(b, null, None)).Items);

        var store = new PublishingSettingsStore(_db);
        await store.SaveScheduleAsync(b, new YouTubeScheduleSettings(["12:00"], "UTC"), None);
        Assert.Equal(["12:00"], (await store.GetScheduleAsync(b, None)).Slots);
        Assert.Equal(YouTubeScheduleSettings.Default.Slots, (await store.GetScheduleAsync(a, None)).Slots);
    }

    [Fact]
    public async Task APageCannotBeRemovedWhileAClipStillDependsOnIt()
    {
        var page = Page("@TJRDaily");
        var clip = Clip(page, DateTimeOffset.UtcNow.AddHours(3));
        _tjr.YouTubeConnectionId = page;
        _db.SaveChanges();

        var (found, blocked) = await Connections().DeleteAsync(PlatformKind.YouTubeShorts, page, None);
        Assert.True(found);
        Assert.StartsWith("1 clip is still on this page's schedule.", blocked);
        Assert.Single(_db.PlatformConnections);

        clip.Status = PublishStatus.Published;
        _db.SaveChanges();
        Assert.Equal((true, (string?)null), await Connections().DeleteAsync(PlatformKind.YouTubeShorts, page, None));
        Assert.Empty(_db.PlatformConnections);
        Assert.Null((await _db.Campaigns.SingleAsync(x => x.Id == _tjr.Id)).YouTubeConnectionId);
    }

    [Fact]
    public async Task ANewPageStartsWithTheSameGoogleClientAndNoSignIn()
    {
        Page("@TJRDaily");

        var added = await Connections().AddYouTubePageAsync("@Second", null, None);

        Assert.Null(added.Errors);
        Assert.False(added.Connection!.CanPublish);
        var credentials = await Connections().GetCredentialsAsync(PlatformKind.YouTubeShorts, added.Connection.Id, None);
        Assert.Equal("client", credentials!["clientId"]);
        Assert.Equal("secret", credentials["clientSecret"]);
        Assert.False(credentials.ContainsKey("refreshToken"));
        Assert.NotNull((await Connections().AddYouTubePageAsync("@second", null, None)).Errors);
        Assert.NotNull((await Connections().AddYouTubePageAsync("  ", null, None)).Errors);
    }

    [Fact]
    public async Task ASignInForAChannelAnotherPageAlreadyUsesIsRefused()
    {
        var first = Page("@TJRDaily", minutesAgo: 10);
        var second = (await Connections().AddYouTubePageAsync("@Second", null, None)).Connection!.Id!.Value;
        var states = new YouTubeOAuthStateStore(Options.Create(new PublishingApiOptions()));
        var oauth = new YouTubeOAuthService(new OneClient(_google), Options.Create(new PublishingApiOptions()), states, Connections(), Publisher());
        _google.Channels["access-@TJRDaily"] = ("UC-tjr", "TJRDaily");

        // Google's own screen was left on the first channel, so the new sign-in is for TJRDaily again.
        _google.Channels["new-access"] = ("UC-tjr", "TJRDaily");
        states.AddForTest(new YouTubeOAuthState("state-1", "verifier", Callback, DateTimeOffset.UtcNow.AddMinutes(5), second));
        var refused = await Assert.ThrowsAsync<YouTubePageAlreadyConnectedException>(() => oauth.CompleteAsync("code", "state-1", None));
        Assert.Contains("\"TJRDaily\"", refused.Message);
        Assert.Contains("@TJRDaily", refused.Message);
        Assert.False((await Connections().GetCredentialsAsync(PlatformKind.YouTubeShorts, second, None))!.ContainsKey("refreshToken"));
        // The first page was connected before channels were recorded; the check looked its channel up and kept it.
        Assert.Equal("UC-tjr", (await Connections().GetCredentialsAsync(PlatformKind.YouTubeShorts, first, None))!["channelId"]);

        _google.Channels["new-access"] = ("UC-second", "Second Page");
        states.AddForTest(new YouTubeOAuthState("state-2", "verifier", Callback, DateTimeOffset.UtcNow.AddMinutes(5), second));
        await oauth.CompleteAsync("code", "state-2", None);

        var connected = await Connections().GetAsync(PlatformKind.YouTubeShorts, second, None);
        Assert.True(connected.CanPublish);
        Assert.Equal("Second Page", connected.ChannelTitle);
        Assert.Equal("@Second (Second Page)", (await Schedule().GetAsync(second, null, None)).Channel);
        Assert.Equal("access-@TJRDaily", (await Connections().GetCredentialsAsync(PlatformKind.YouTubeShorts, first, None))!["accessToken"]);
    }

    private sealed class MemoryVault : IPublishingCredentialVault
    {
        private readonly Dictionary<string, Dictionary<string, string>> _stored = [];

        public string Store(PlatformKind platform, IReadOnlyDictionary<string, string> credentials)
        {
            var reference = $"vault-{Guid.NewGuid():N}";
            _stored[reference] = new(credentials, StringComparer.OrdinalIgnoreCase);
            return reference;
        }

        public Dictionary<string, string> Read(string reference) =>
            _stored.TryGetValue(reference, out var found) ? new(found, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);

        public void Delete(string reference) => _stored.Remove(reference);
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>Plays Google: hands out a sign-in, and says which channel each access token belongs to.</summary>
    private sealed class GoogleHandler : HttpMessageHandler
    {
        public Dictionary<string, (string Id, string Title)> Channels { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.Host == "oauth2.googleapis.com")
                return Json(JsonSerializer.Serialize(new
                {
                    access_token = "new-access", refresh_token = "new-refresh", expires_in = 3600,
                    scope = $"{YouTubeOAuthService.UploadScope} {YouTubeOAuthService.ManageScope}",
                }));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal))
                return Channels.TryGetValue(request.Headers.Authorization?.Parameter ?? "", out var channel)
                    ? Json(JsonSerializer.Serialize(new { items = new[] { new { id = channel.Id, snippet = new { title = channel.Title } } } }))
                    : Json("""{"items":[]}""");
            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
