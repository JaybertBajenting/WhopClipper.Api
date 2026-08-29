using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class PublishingSecurityTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("twoFactorSeed")]
    [InlineData("otp")]
    [InlineData("recoveryCodes")]
    [InlineData("securityAnswer")]
    public void AccountKitRejectsAuthenticationSecrets(string key)
    {
        var errors = PlatformAccountKitService.Validate(new Dictionary<string, string> { [key] = "canary-secret" }, PlatformKind.TikTok);

        Assert.NotEmpty(errors);
        Assert.DoesNotContain("canary-secret", JsonSerializer.Serialize(errors));
    }

    [Fact]
    public void ConnectionRequestRejectsUnknownPasswordWithoutEchoingIt()
    {
        var request = new SavePlatformConnectionRequest
        {
            AdditionalFields = new() { ["accountPassword"] = JsonSerializer.SerializeToElement("super-secret-canary") }
        };

        var method = typeof(PlatformConnectionService).GetMethod("Validate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var errors = Assert.IsType<Dictionary<string, string[]>>(method.Invoke(null, [PlatformKind.YouTubeShorts, request]));

        Assert.Contains("accountPassword", errors.Keys);
        Assert.DoesNotContain("super-secret-canary", JsonSerializer.Serialize(errors));
    }

    [Fact]
    public void PublishingNonceIsRequiredAndComparedExactly()
    {
        var guard = new LocalPublishingRequestGuard();
        var valid = new DefaultHttpContext();
        valid.Request.Headers[LocalPublishingRequestGuard.HeaderName] = guard.Nonce;
        var missing = new DefaultHttpContext();
        var changed = new DefaultHttpContext();
        changed.Request.Headers[LocalPublishingRequestGuard.HeaderName] = guard.Nonce + "x";

        Assert.True(guard.IsValid(valid));
        Assert.False(guard.IsValid(missing));
        Assert.False(guard.IsValid(changed));
    }

    [Fact]
    public void OAuthStateUsesPkceAndCanOnlyBeConsumedOnce()
    {
        var store = new YouTubeOAuthStateStore(Options.Create(new PublishingApiOptions { OAuthStateMinutes = 10 }));
        var state = store.Create("http://127.0.0.1:51991/api/publishing/oauth/youtube/callback");

        Assert.True(state.State.Length >= 40);
        Assert.True(state.CodeVerifier.Length >= 43);
        Assert.NotEqual(state.CodeVerifier, state.CodeChallenge);
        Assert.NotNull(store.Consume(state.State));
        Assert.Null(store.Consume(state.State));
    }

    [Fact]
    public void ExpiredOAuthStateIsRejected()
    {
        var store = new YouTubeOAuthStateStore(Options.Create(new PublishingApiOptions()));
        var expired = new YouTubeOAuthState("expired-state", "verifier", "http://127.0.0.1:5000/callback", DateTimeOffset.UtcNow.AddSeconds(-1));
        store.AddForTest(expired);

        Assert.Null(store.Consume(expired.State));
    }

    [Theory]
    [InlineData("Bearer very-secret-token", "very-secret-token")]
    [InlineData("access_token=canary-token", "canary-token")]
    [InlineData("client_secret: top-secret", "top-secret")]
    public void ErrorsRedactSecretMaterial(string input, string forbidden)
    {
        var cleaned = PlatformConnectionService.CleanError(input);

        Assert.NotNull(cleaned);
        Assert.DoesNotContain(forbidden, cleaned!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", cleaned!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestFingerprintBindsEveryMaterialPublishingChoice()
    {
        var campaign = Guid.NewGuid();
        var baseline = PublishJobService.ComputeRequestHash(campaign, PlatformKind.YouTubeShorts,
            "campaigns/a/output/clip.mp4", "Title", "Caption #ad", "private", false, false, "filehash", "@channel-a");
        var changedAccount = PublishJobService.ComputeRequestHash(campaign, PlatformKind.YouTubeShorts,
            "campaigns/a/output/clip.mp4", "Title", "Caption #ad", "private", false, false, "filehash", "@channel-b");
        var changedCaption = PublishJobService.ComputeRequestHash(campaign, PlatformKind.YouTubeShorts,
            "campaigns/a/output/clip.mp4", "Title", "Different #ad", "private", false, false, "filehash", "@channel-a");
        var changedFile = PublishJobService.ComputeRequestHash(campaign, PlatformKind.YouTubeShorts,
            "campaigns/a/output/clip.mp4", "Title", "Caption #ad", "private", false, false, "otherhash", "@channel-a");

        Assert.Equal(64, baseline.Length);
        Assert.NotEqual(baseline, changedAccount);
        Assert.NotEqual(baseline, changedCaption);
        Assert.NotEqual(baseline, changedFile);
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@creator/video/123", true)]
    [InlineData("https://vm.tiktok.com/abc", true)]
    [InlineData("http://www.tiktok.com/@creator/video/123", false)]
    [InlineData("https://eviltiktok.com/@creator/video/123", false)]
    [InlineData("https://tiktok.com.example.test/video/123", false)]
    public void ManualCompletionAcceptsOnlyHttpsTikTokLinks(string url, bool expected)
    {
        Assert.Equal(expected, PublishJobService.TryNormalizeTikTokUrl(url, out _));
    }

    [Fact]
    public void PlatformReadinessRequiresExactAccountAndSupportedInstagramMode()
    {
        var youtubeWithoutAccount = PlatformConnectionService.Readiness(PlatformKind.YouTubeShorts,
            new Dictionary<string, string> { ["clientId"] = "client", ["refreshToken"] = "refresh" }, null, null);
        var instagramWrongMode = PlatformConnectionService.Readiness(PlatformKind.InstagramReels,
            new Dictionary<string, string> { ["accessToken"] = "token", ["accountId"] = "ig", ["pageId"] = "page", ["authMode"] = "InstagramLogin" }, null, null);

        Assert.False(youtubeWithoutAccount.CanPublish);
        Assert.Contains("exact", youtubeWithoutAccount.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(instagramWrongMode.CanPublish);
        Assert.Contains("FacebookLoginForBusiness", instagramWrongMode.Message, StringComparison.Ordinal);
    }
}
