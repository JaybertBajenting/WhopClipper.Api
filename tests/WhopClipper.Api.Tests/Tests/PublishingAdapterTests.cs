using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class PublishingAdapterTests : IDisposable
{
    private readonly string _videoPath = Path.Combine(Path.GetTempPath(), $"whop-publish-{Guid.NewGuid():N}.mp4");

    public PublishingAdapterTests() => File.WriteAllBytes(_videoPath, Enumerable.Range(0, 4096).Select(x => (byte)(x % 251)).ToArray());

    [Fact]
    public async Task YouTubeUsesAuthorizedResumableInitializationAndVideoContentType()
    {
        var handler = new SequenceHandler([
            async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("uploadType=resumable", request.RequestUri!.Query);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("access-canary", request.Headers.Authorization?.Parameter);
                var metadata = await request.Content!.ReadAsStringAsync();
                Assert.Contains("\"privacyStatus\":\"private\"", metadata);
                return Response(HttpStatusCode.OK, "{}", location: "https://upload.googleapis.com/resumable/session-1");
            },
            async request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("upload.googleapis.com", request.RequestUri!.Host);
                Assert.Equal("video/mp4", request.Content!.Headers.ContentType?.MediaType);
                Assert.Equal(new FileInfo(_videoPath).Length, request.Content.Headers.ContentLength);
                Assert.Equal(new FileInfo(_videoPath).Length, (await request.Content.ReadAsByteArrayAsync()).LongLength);
                return Response(HttpStatusCode.OK, "{\"id\":\"video-123\"}");
            }
        ]);
        var publisher = new YouTubePublisher(new SingleClientFactory(handler), Options.Create(new PublishingApiOptions()));
        var credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientId"] = "client", ["accountId"] = "@channel", ["accessToken"] = "access-canary",
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        };

        var result = await publisher.PublishAsync(new(Guid.NewGuid(), _videoPath, "Title", "Caption", "private", false, false),
            credentials, new Progress<double>(), CancellationToken.None);

        Assert.Equal("video-123", result.ExternalId);
        Assert.Equal("https://youtu.be/video-123", result.ExternalUrl);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AScheduledUploadStaysPrivateUntilItsPublishAt()
    {
        var slot = new DateTimeOffset(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);
        var handler = new SequenceHandler([
            async request =>
            {
                var metadata = await request.Content!.ReadAsStringAsync();
                Assert.Contains("\"privacyStatus\":\"private\"", metadata);
                Assert.Contains("\"publishAt\":\"2026-09-26T15:00:00Z\"", metadata);
                return Response(HttpStatusCode.OK, "{}", location: "https://upload.googleapis.com/resumable/session-2");
            },
            _ => Task.FromResult(Response(HttpStatusCode.OK, "{\"id\":\"video-456\"}"))
        ]);
        var publisher = new YouTubePublisher(new SingleClientFactory(handler), Options.Create(new PublishingApiOptions()));
        var credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientId"] = "client", ["accessToken"] = "access", ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        };

        var result = await publisher.PublishAsync(new(Guid.NewGuid(), _videoPath, "Title", "Caption", "public", false, false, slot),
            credentials, new Progress<double>(), CancellationToken.None);

        Assert.Equal("video-456", result.ExternalId);
    }

    [Fact]
    public async Task YouTubeDryRunMakesNoExternalRequest()
    {
        var handler = new SequenceHandler([]);
        var publisher = new YouTubePublisher(new SingleClientFactory(handler), Options.Create(new PublishingApiOptions()));

        var result = await publisher.PublishAsync(new(Guid.NewGuid(), _videoPath, "Title", "Caption", "private", false, true),
            new(StringComparer.OrdinalIgnoreCase), new Progress<double>(), CancellationToken.None);

        Assert.Equal("DryRun", result.ResultCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task YouTubeRejectsUntrustedResumableLocationBeforeSendingVideoOrToken()
    {
        var handler = new SequenceHandler([
            _ => Task.FromResult(Response(HttpStatusCode.OK, "{}", location: "https://evilgoogleapis.com/session"))
        ]);
        var publisher = new YouTubePublisher(new SingleClientFactory(handler), Options.Create(new PublishingApiOptions()));
        var credentials = new Dictionary<string, string>
        {
            ["clientId"] = "client", ["accountId"] = "@channel", ["accessToken"] = "token",
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        };

        var error = await Assert.ThrowsAsync<PublishingApiException>(() => publisher.PublishAsync(
            new(Guid.NewGuid(), _videoPath, "Title", "Caption", "private", false, false), credentials,
            new Progress<double>(), CancellationToken.None));

        Assert.Contains("unsafe", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task InstagramPreparesThenWaitsUntilSeparateFinalPublishCall()
    {
        var handler = new SequenceHandler([
            async request =>
            {
                Assert.Equal("/v26.0/ig-123/media", request.RequestUri!.AbsolutePath);
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains("upload_type=resumable", form);
                Assert.Contains("caption=Caption", form);
                return Response(HttpStatusCode.OK, "{\"id\":\"container-1\",\"uri\":\"https://rupload.facebook.com/video-upload/session-1\"}");
            },
            async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("rupload.facebook.com", request.RequestUri!.Host);
                Assert.Equal("OAuth page-token", request.Headers.GetValues("Authorization").Single());
                Assert.Equal("video/mp4", request.Content!.Headers.ContentType?.MediaType);
                _ = await request.Content.ReadAsByteArrayAsync();
                return Response(HttpStatusCode.OK, "{}");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/v26.0/container-1", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Response(HttpStatusCode.OK, "{\"status_code\":\"FINISHED\"}"));
            },
            async request =>
            {
                Assert.Equal("/v26.0/ig-123/media_publish", request.RequestUri!.AbsolutePath);
                Assert.Contains("creation_id=container-1", await request.Content!.ReadAsStringAsync());
                return Response(HttpStatusCode.OK, "{\"id\":\"media-9\"}");
            }
        ]);
        var publisher = new InstagramPublisher(new SingleClientFactory(handler), Options.Create(new PublishingApiOptions()));
        var credentials = new Dictionary<string, string>
        {
            ["accessToken"] = "page-token", ["accountId"] = "ig-123", ["pageId"] = "page-123",
            ["authMode"] = "FacebookLoginForBusiness"
        };

        var prepared = await publisher.PrepareAsync(new(Guid.NewGuid(), _videoPath, "Title", "Caption", "public", true, false),
            credentials, new Progress<double>(), CancellationToken.None);

        Assert.Equal("container-1", prepared.ContainerId);
        Assert.Equal(3, handler.CallCount);

        var published = await publisher.FinalizeAsync(Guid.NewGuid(), prepared.ContainerId, credentials,
            new Progress<double>(), CancellationToken.None);

        Assert.Equal("media-9", published.ExternalId);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public void InstagramLocalUploadRequiresExplicitFacebookLoginForBusinessPrerequisites()
    {
        var credentials = new Dictionary<string, string>
        {
            ["accessToken"] = "token", ["accountId"] = "ig", ["pageId"] = "page", ["authMode"] = "InstagramLogin"
        };

        Assert.Throws<PublishingApiException>(() => InstagramPublisher.RequireFacebookLoginForBusiness(credentials));
    }

    public void Dispose()
    {
        if (File.Exists(_videoPath)) File.Delete(_videoPath);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string? location = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (location is not null) response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class SequenceHandler(IEnumerable<Func<HttpRequestMessage, Task<HttpResponseMessage>>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new(responses);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected external HTTP request.");
            return _responses.Dequeue()(request);
        }
    }
}
