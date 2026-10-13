using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class PhoneCompanionTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string AndroidConfig = """
        {"project_info":{"project_id":"phone-test","project_number":"123456"},"client":[
        {"client_info":{"mobilesdk_app_id":"1:123456:android:abcdef","android_client_info":{"package_name":"com.whopclipper.phone"}},
        "api_key":[{"current_key":"AIza-synthetic-test-value"}]}]}
        """;
    [Fact]
    public void ReadsTheSameAuthenticatedVectorAsAndroid()
    {
        var encrypted = new PhoneEncryptedSchedule("AAECAwQFBgcICQoL", "PCCgfreWq3TjY626ncsbAeqm9BbKIAIBdwxvOiD2kvpzldxiRODRTA==");
        Assert.Equal("{\"version\":1,\"clips\":[]}", Encoding.UTF8.GetString(PhoneScheduleCrypto.Decrypt(encrypted, Key, "pairing", "revision")));
        Assert.Throws<AuthenticationTagMismatchException>(() => PhoneScheduleCrypto.Decrypt(encrypted, Key, "wrong", "revision"));
        Assert.Throws<AuthenticationTagMismatchException>(() => PhoneScheduleCrypto.Decrypt(encrypted, Key, "pairing", "wrong"));
    }
    [Fact]
    public void EncryptsWithFreshNoncesAndRejectsTampering()
    {
        var plain = Encoding.UTF8.GetBytes("private title and campaign link");
        var first = PhoneScheduleCrypto.Encrypt(plain, Key, "pairing", "revision");
        var second = PhoneScheduleCrypto.Encrypt(plain, Key, "pairing", "revision");
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal(plain, PhoneScheduleCrypto.Decrypt(first, Key, "pairing", "revision"));
        var bytes = Convert.FromBase64String(first.Ciphertext); bytes[0] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => PhoneScheduleCrypto.Decrypt(first with { Ciphertext = Convert.ToBase64String(bytes) }, Key, "pairing", "revision"));
    }
    [Fact]
    public void AndroidSettingsMustMatchOurPackage()
    {
        Assert.Equal("phone-test", PhoneCompanionService.ParseAndroidConfig(AndroidConfig).ProjectId);
        Assert.Throws<InvalidOperationException>(() => PhoneCompanionService.ParseAndroidConfig(AndroidConfig.Replace("com.whopclipper.phone", "another.app")));
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///D:/secrets")]
    [InlineData("https://user:password@example.com")]
    public void UnsafeCampaignLinksAreNotSentToPhone(string value) => Assert.Null(PhoneCompanionService.SafeCampaignUrl(value));
    [Fact]
    public async Task SnapshotKeepsPagesSeparateAndUsesCurrentCampaignLinks()
    {
        using var db = Database(); var vault = new MemoryVault(); var handler = new CloudHandler();
        var a = new PlatformConnection { Platform = PlatformKind.YouTubeShorts, AccountIdentifier = "@first",
            CredentialReference = vault.Store(PlatformKind.YouTubeShorts, new Dictionary<string, string> { ["channelId"] = "UC-first", ["refreshToken"] = "never-send-this" }) };
        var b = new PlatformConnection { Platform = PlatformKind.YouTubeShorts, AccountIdentifier = "@second" };
        var campaign = new Campaign { Name = "Current campaign", WhopUrl = "https://whop.com/new-link" };
        db.PlatformConnections.AddRange(a, b); db.Campaigns.Add(campaign);
        foreach (var (page, status) in new[] { (a, PublishStatus.Scheduled), (b, PublishStatus.Scheduled), (a, PublishStatus.Removed), (b, PublishStatus.Cancelled) })
            db.PublishJobs.Add(new PublishJob { Campaign = campaign, Platform = PlatformKind.YouTubeShorts, ConnectionId = page.Id,
                ScheduledPublishAt = DateTimeOffset.UtcNow.AddDays(7), ExternalId = "abcdefghijk", Status = status, Title = "Clip" });
        await db.SaveChangesAsync();
        var service = new PhoneCompanionService(db, vault, new Factory(handler), new PhoneCompanionRuntime());
        var clips = await service.BuildScheduleAsync(CancellationToken.None);
        Assert.Equal(2, clips.Count); Assert.Contains(clips, x => x.PageName == "@first"); Assert.Contains(clips, x => x.PageName == "@second");
        Assert.All(clips, x => Assert.Equal(campaign.WhopUrl, x.CampaignUrl));
        Assert.DoesNotContain("never-send-this", JsonSerializer.Serialize(clips));
    }
    [Fact]
    public async Task CloudGetsEncryptedScheduleAndUnchangedScheduleDoesNotWriteAgain()
    {
        using var db = Database(); var vault = new MemoryVault(); var handler = new CloudHandler();
        var campaign = new Campaign { Name = "secret campaign", WhopUrl = "https://whop.com/private-campaign" };
        db.Campaigns.Add(campaign); db.PublishJobs.Add(new PublishJob { Campaign = campaign, Platform = PlatformKind.YouTubeShorts,
            Status = PublishStatus.Scheduled, ScheduledPublishAt = DateTimeOffset.UtcNow.AddDays(5), ExternalId = "abcdefghijk", Title = "secret clip title" });
        await db.SaveChangesAsync();
        var service = new PhoneCompanionService(db, vault, new Factory(handler), new PhoneCompanionRuntime());
        await service.ConfigureAsync(ServiceAccount(), AndroidConfig, CancellationToken.None);
        var write = Assert.Single(handler.Writes);
        Assert.DoesNotContain("secret clip title", write); Assert.DoesNotContain("private-campaign", write);
        Assert.DoesNotContain("PRIVATE KEY", write);
        var pairing = await service.PairingAsync(CancellationToken.None);
        Assert.StartsWith("whopclipper://pair?data=", pairing);
        var json = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(pairing.Split("data=")[1]);
        using var p = JsonDocument.Parse(json); using var document = JsonDocument.Parse(write);
        var fields = document.RootElement.GetProperty("fields");
        string F(string name) => fields.GetProperty(name).GetProperty("stringValue").GetString()!;
        var plain = PhoneScheduleCrypto.Decrypt(new(F("nonce"), F("ciphertext")), p.RootElement.GetProperty("encryptionKey").GetString()!,
            p.RootElement.GetProperty("pairingId").GetString()!, F("revision"));
        Assert.Contains("secret clip title", Encoding.UTF8.GetString(plain));
        await service.SyncAsync(false, CancellationToken.None); Assert.Single(handler.Writes);
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(json));
    }
    [Fact]
    public async Task FailedRemoteRevocationKeepsExistingPairingKeys()
    {
        using var db = Database(); var vault = new MemoryVault(); var handler = new CloudHandler();
        var service = new PhoneCompanionService(db, vault, new Factory(handler), new PhoneCompanionRuntime());
        await service.ConfigureAsync(ServiceAccount(), AndroidConfig, CancellationToken.None);
        var old = await service.PairingAsync(CancellationToken.None);
        handler.FailWrites = true;
        await Assert.ThrowsAsync<PhoneCloudException>(() => service.RotateAsync(CancellationToken.None));
        Assert.Equal(old, await service.PairingAsync(CancellationToken.None));
    }
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static string ServiceAccount()
    {
        using var rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new { type = "service_account", project_id = "phone-test",
            client_email = "test@phone-test.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem() });
    }
    private sealed class MemoryVault : IPublishingCredentialVault
    {
        private readonly Dictionary<string, Dictionary<string, string>> values = new();
        public string Store(PlatformKind platform, IReadOnlyDictionary<string, string> credentials) { var id = Guid.NewGuid().ToString(); values[id] = new(credentials); return id; }
        public Dictionary<string, string> Read(string reference) => values[reference];
        public void Delete(string reference) => values.Remove(reference);
    }
    private sealed class Factory(CloudHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class CloudHandler : HttpMessageHandler
    {
        public List<string> Writes { get; } = [];
        public bool FailWrites { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com") return Response("{\"access_token\":\"test-access\",\"expires_in\":3600}");
            if (request.Method == HttpMethod.Patch)
            {
                if (FailWrites) return new(HttpStatusCode.ServiceUnavailable);
                Writes.Add(await request.Content!.ReadAsStringAsync(ct));
            }
            return Response("{}");
        }
        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
