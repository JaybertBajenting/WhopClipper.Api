using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using WorkstationGame.Api.Services;
using Xunit;

namespace WorkstationGame.Api.Tests;

public sealed class SupabaseStorageServiceTests
{
    [Fact]
    public async Task Upload_WithNewSecretKey_UsesApiKeyHeaderOnly()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler, "sb_secret_test-key");

        await service.UploadAsync(CreateImage(), CancellationToken.None);

        Assert.Equal("sb_secret_test-key", handler.ApiKey);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Upload_WithLegacyServiceRoleKey_UsesApiKeyAndBearerHeaders()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler, "eyJlegacy-service-role-jwt");

        await service.UploadAsync(CreateImage(), CancellationToken.None);

        Assert.Equal("eyJlegacy-service-role-jwt", handler.ApiKey);
        Assert.Equal("Bearer eyJlegacy-service-role-jwt", handler.Authorization);
    }

    private static SupabaseStorageService CreateService(HttpMessageHandler handler, string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Game:SupabaseUrl"] = "https://example.supabase.co",
                ["Game:SupabaseServiceRoleKey"] = key,
                ["Game:SupabaseStorageBucket"] = "workstations"
            })
            .Build();

        return new SupabaseStorageService(new HttpClient(handler), configuration);
    }

    private static FormFile CreateImage()
    {
        var stream = new MemoryStream([1, 2, 3]);
        return new FormFile(stream, 0, stream.Length, "file", "desk.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.TryGetValues("apikey", out var values)
                ? values.Single()
                : null;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
