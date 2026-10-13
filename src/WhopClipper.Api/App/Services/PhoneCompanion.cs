using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed record PhoneCompanionStatus(bool Configured, string? ProjectId, DateTimeOffset? LastSyncedAt,
    string? LastError, int DeviceCount, string PackageName = "com.whopclipper.phone");
public sealed record PhoneFirebaseConfig(string ProjectId, string AppId, string ApiKey, string SenderId);
public sealed record PhoneCompanionState(string VaultReference, PhoneFirebaseConfig Firebase,
    string? PayloadHash = null, DateTimeOffset? LastSyncedAt = null);
/// <param name="Thumb">A small still of the clip as base64 JPEG, for a clip that is not public yet
/// (<see cref="PhoneThumbnails"/>). Null for a live clip: the phone gets that one from YouTube.</param>
public sealed record PhoneClip(string Id, string Title, string VideoId, long PublishAt, string PageId,
    string PageName, string ChannelId, string CampaignName, string? CampaignUrl, string Status, string? Thumb = null);
public sealed record PhoneEncryptedSchedule(string Nonce, string Ciphertext);

public sealed class PhoneCompanionRuntime
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string? LastError { get; set; }
    public int DeviceCount { get; set; }
    public string? TokenReference { get; set; }
    public string? AccessToken { get; set; }
    public DateTimeOffset TokenExpiresAt { get; set; }
}

/// <summary>Versioned authenticated encryption shared with the native Android reader.</summary>
public static class PhoneScheduleCrypto
{
    public static PhoneEncryptedSchedule Encrypt(byte[] payload, string keyBase64, string pairingId, string revision)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var encrypted = new byte[payload.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(keyBase64), 16);
        aes.Encrypt(nonce, payload, encrypted, tag, Encoding.UTF8.GetBytes($"whopclipper-phone-v1:{pairingId}:{revision}"));
        return new(Convert.ToBase64String(nonce), Convert.ToBase64String([.. encrypted, .. tag]));
    }
    public static byte[] Decrypt(PhoneEncryptedSchedule encrypted, string keyBase64, string pairingId, string revision)
    {
        var bytes = Convert.FromBase64String(encrypted.Ciphertext);
        var plain = new byte[bytes.Length - 16];
        using var aes = new AesGcm(Convert.FromBase64String(keyBase64), 16);
        aes.Decrypt(Convert.FromBase64String(encrypted.Nonce), bytes.AsSpan(0, plain.Length), bytes.AsSpan(plain.Length),
            plain, Encoding.UTF8.GetBytes($"whopclipper-phone-v1:{pairingId}:{revision}"));
        return plain;
    }
}

public sealed class PhoneCompanionService(AppDbContext db, IPublishingCredentialVault vault,
    IHttpClientFactory clients, PhoneCompanionRuntime runtime, PhoneThumbnails? thumbnails = null)
{
    private const string SettingsKey = "phone.firebase.v1";
    public const int MaxConfigBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private async Task<PhoneCompanionState?> StateAsync(CancellationToken ct)
    {
        var value = await db.ApplicationSettings.AsNoTracking().Where(x => x.Key == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return value is null ? null : JsonSerializer.Deserialize<PhoneCompanionState>(value, Json);
    }
    private async Task SaveAsync(PhoneCompanionState state, CancellationToken ct)
    {
        var row = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == SettingsKey, ct);
        if (row is null) db.ApplicationSettings.Add(row = new ApplicationSetting { Key = SettingsKey });
        row.Value = JsonSerializer.Serialize(state, Json); row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
    public async Task TrySyncAsync(CancellationToken ct)
    {
        try { await SyncAsync(false, ct); }
        catch (Exception)
        { runtime.LastError ??= "Phone sync is waiting for Firebase. The PC will retry; check the Phone app card before closing."; }
    }
    public async Task<PhoneCompanionStatus> StatusAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct);
        return new(state is not null, state?.Firebase.ProjectId, state?.LastSyncedAt, runtime.LastError, runtime.DeviceCount);
    }
    public static PhoneFirebaseConfig ParseAndroidConfig(string androidJson)
    {
        using var config = JsonDocument.Parse(androidJson);
        var project = config.RootElement.GetProperty("project_info");
        var client = config.RootElement.GetProperty("client").EnumerateArray().FirstOrDefault(x =>
            x.GetProperty("client_info").GetProperty("android_client_info").GetProperty("package_name").GetString() == "com.whopclipper.phone");
        if (client.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Register the Android package com.whopclipper.phone in Firebase, then download its google-services.json.");
        var result = new PhoneFirebaseConfig(project.GetProperty("project_id").GetString()!,
            client.GetProperty("client_info").GetProperty("mobilesdk_app_id").GetString()!,
            client.GetProperty("api_key")[0].GetProperty("current_key").GetString()!, project.GetProperty("project_number").GetString()!);
        if (!Regex.IsMatch(result.ProjectId, "^[a-z][a-z0-9-]{4,62}$") || !result.AppId.StartsWith("1:")
            || !result.ApiKey.StartsWith("AIza") || !Regex.IsMatch(result.SenderId, "^[0-9]+$"))
            throw new InvalidOperationException("The Firebase Android settings are invalid.");
        return result;
    }
    public async Task ConfigureAsync(string serviceJson, string androidJson, CancellationToken ct)
    {
        var firebase = ParseAndroidConfig(androidJson);
        using var service = JsonDocument.Parse(serviceJson);
        var root = service.RootElement;
        if (root.GetProperty("type").GetString() != "service_account" || root.GetProperty("project_id").GetString() != firebase.ProjectId)
            throw new InvalidOperationException("Choose a Firebase service-account key and Android settings from the same project.");
        // Validate format without returning, logging or displaying the private key.
        using (var rsa = RSA.Create()) rsa.ImportFromPem(root.GetProperty("private_key").GetString()!);
        if (!root.GetProperty("client_email").GetString()!.EndsWith(".iam.gserviceaccount.com", StringComparison.Ordinal))
            throw new InvalidOperationException("The service-account key is invalid.");
        await runtime.Gate.WaitAsync(ct);
        try
        {
            var old = await StateAsync(ct);
            if (old is not null) await RevokeAsync(old, vault.Read(old.VaultReference), ct);
            var secret = NewSecrets(serviceJson);
            var reference = vault.Store(PlatformKind.YouTubeShorts, secret);
            try { await SaveAsync(new(reference, firebase), ct); }
            catch { vault.Delete(reference); throw; }
            if (old is not null) vault.Delete(old.VaultReference);
            runtime.LastError = null; runtime.DeviceCount = 0;
        }
        finally { runtime.Gate.Release(); }
        await SyncAsync(force: true, ct);
    }
    private static Dictionary<string, string> NewSecrets(string serviceJson) => new()
    {
        ["firebaseServiceAccount"] = serviceJson,
        ["pairingId"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        ["joinSecret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        ["encryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    };
    public async Task RotateAsync(CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct);
        try
        {
            var state = await StateAsync(ct) ?? throw new InvalidOperationException("Set up Firebase first.");
            var previous = vault.Read(state.VaultReference);
            // Revocation must succeed before the old local credential can be discarded.
            await RevokeAsync(state, previous, ct);
            var reference = vault.Store(PlatformKind.YouTubeShorts, NewSecrets(previous["firebaseServiceAccount"]));
            try { await SaveAsync(state with { VaultReference = reference, PayloadHash = null, LastSyncedAt = null }, ct); }
            catch { vault.Delete(reference); throw; }
            vault.Delete(state.VaultReference); runtime.DeviceCount = 0;
        }
        finally { runtime.Gate.Release(); }
        await SyncAsync(force: true, ct);
    }
    public async Task<string> PairingAsync(CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct);
        try
        {
            var state = await StateAsync(ct) ?? throw new InvalidOperationException("Set up Firebase first.");
            if (state.LastSyncedAt is null) throw new InvalidOperationException("Sync the schedule to Firebase before pairing.");
            var keys = vault.Read(state.VaultReference);
            var json = JsonSerializer.Serialize(new { version = 1, state.Firebase.ProjectId, state.Firebase.AppId,
                state.Firebase.ApiKey, state.Firebase.SenderId, pairingId = keys["pairingId"], joinSecret = keys["joinSecret"], encryptionKey = keys["encryptionKey"] }, Json);
            return "whopclipper://pair?data=" + WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        }
        finally { runtime.Gate.Release(); }
    }
    public async Task SyncAsync(bool force, CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct);
        try
        {
            var state = await StateAsync(ct);
            if (state is null) return;
            var clips = await BuildScheduleAsync(ct);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, clips }, Json);
            if (payload.Length > 700_000) throw new InvalidOperationException("Phone schedule exceeds Firebase's document size. Reduce retained scheduled clips.");
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (!force && state.PayloadHash == hash) return;
            var keys = vault.Read(state.VaultReference);
            var encrypted = PhoneScheduleCrypto.Encrypt(payload, keys["encryptionKey"], keys["pairingId"], hash);
            var fields = new Dictionary<string, object>
            {
                ["active"] = new { booleanValue = true }, ["joinSecret"] = StringValue(keys["joinSecret"]),
                ["revision"] = StringValue(hash), ["nonce"] = StringValue(encrypted.Nonce), ["ciphertext"] = StringValue(encrypted.Ciphertext)
            };
            var token = await AccessTokenAsync(state, keys, ct);
            await CloudAsync(HttpMethod.Patch, DocumentUrl(state, keys), new { fields }, token, ct);
            // If push fails, the next poll retries. Only successful cloud writes are marked synced.
            await NudgeAsync(state, keys, token, test: false, ct);
            await SaveAsync(state with { PayloadHash = hash, LastSyncedAt = DateTimeOffset.UtcNow }, ct);
            runtime.LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or CryptographicException or JsonException)
        {
            runtime.LastError = ex is InvalidOperationException ? ex.Message : "Firebase sync failed. Check internet and Firebase setup; the PC will retry.";
            throw;
        }
        finally { runtime.Gate.Release(); }
    }
    public async Task<IReadOnlyList<PhoneClip>> BuildScheduleAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var jobs = await db.PublishJobs.AsNoTracking().Include(x => x.Campaign)
            .Where(x => x.Platform == PlatformKind.YouTubeShorts && !x.DryRun && x.ScheduledPublishAt != null
                && (x.Status == PublishStatus.Scheduled || x.Status == PublishStatus.Published || x.Status == PublishStatus.Queued
                    || x.Status == PublishStatus.Validating || x.Status == PublishStatus.Uploading || x.Status == PublishStatus.Processing)
                && (x.ScheduledPublishAt >= since || x.Status != PublishStatus.Published))
            .OrderBy(x => x.ScheduledPublishAt).ThenBy(x => x.Id).Take(2001).ToListAsync(ct);
        if (jobs.Count > 2000) throw new InvalidOperationException("The phone schedule supports up to 2,000 clips. Archive old scheduled clips first.");
        var pages = await db.PlatformConnections.AsNoTracking().Where(x => x.Platform == PlatformKind.YouTubeShorts).ToListAsync(ct);
        var labels = new Dictionary<Guid, (string Name, string ChannelId)>();
        foreach (var page in pages)
        {
            var credentials = string.IsNullOrWhiteSpace(page.CredentialReference) ? new Dictionary<string, string>() : vault.Read(page.CredentialReference);
            labels[page.Id] = (PlatformConnectionService.PageLabel(page.AccountIdentifier, credentials.GetValueOrDefault("channelTitle")), credentials.GetValueOrDefault("channelId") ?? "");
        }
        // A clip that is not public yet cannot be seen on YouTube, so its card gets a still from the file here.
        var stills = new List<string?>();
        foreach (var job in jobs)
            stills.Add(thumbnails is null || job.Status == PublishStatus.Published ? null : await thumbnails.GetAsync(job, ct));
        var sent = PhoneThumbnails.WithinBudget(stills);
        return jobs.Select((x, index) =>
        {
            var pageId = x.ConnectionId ?? (pages.Count == 1 ? pages[0].Id : Guid.Empty);
            var label = labels.GetValueOrDefault(pageId, (x.AccountIdentifier ?? "YouTube page", ""));
            return new PhoneClip(x.Id.ToString(), x.Title, x.ExternalId ?? "", x.ScheduledPublishAt!.Value.ToUnixTimeMilliseconds(),
                pageId.ToString(), label.Item1, label.Item2, x.Campaign.Name, SafeCampaignUrl(x.Campaign.WhopUrl), x.Status.ToString(),
                sent[index]);
        }).ToList();
    }
    internal static string? SafeCampaignUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    public async Task TestPushAsync(CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct);
        try
        {
            var state = await StateAsync(ct) ?? throw new InvalidOperationException("Set up Firebase and pair the phone first.");
            var keys = vault.Read(state.VaultReference);
            await NudgeAsync(state, keys, await AccessTokenAsync(state, keys, ct), test: true, ct);
        }
        finally { runtime.Gate.Release(); }
    }
    private async Task RevokeAsync(PhoneCompanionState state, Dictionary<string, string> keys, CancellationToken ct) =>
        await CloudAsync(HttpMethod.Patch, DocumentUrl(state, keys), new { fields = new Dictionary<string, object>
            { ["active"] = new { booleanValue = false }, ["ciphertext"] = StringValue("") } }, await AccessTokenAsync(state, keys, ct), ct);
    private async Task NudgeAsync(PhoneCompanionState state, Dictionary<string, string> keys, string token, bool test, CancellationToken ct)
    {
        var devices = new List<(string Name, string Token)>();
        string? pageToken = null;
        do
        {
            var url = DocumentUrl(state, keys) + "/devices?pageSize=100" + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var result = await CloudAsync(HttpMethod.Get, url, null, token, ct);
            if (result.TryGetProperty("documents", out var documents)) foreach (var document in documents.EnumerateArray())
                if (document.GetProperty("fields").TryGetProperty("fcmToken", out var fcm))
                    devices.Add((document.GetProperty("name").GetString()!, fcm.GetProperty("stringValue").GetString()!));
            pageToken = result.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrWhiteSpace(pageToken));
        runtime.DeviceCount = devices.Count;
        if (test && devices.Count == 0) throw new InvalidOperationException("No phone is paired yet. Scan the QR code and sync on your phone first.");
        foreach (var device in devices)
        {
            var message = new { message = new { token = device.Token,
                data = new { type = test ? "test" : "schedule_changed", pairingId = keys["pairingId"] },
                android = new { priority = test ? "HIGH" : "NORMAL", ttl = "3600s", collapse_key = test ? "phone-test" : "schedule" } } };
            try { await CloudAsync(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{state.Firebase.ProjectId}/messages:send", message, token, ct); }
            catch (PhoneCloudException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                await CloudAsync(HttpMethod.Delete, "https://firestore.googleapis.com/v1/" + device.Name, null, token, ct);
                runtime.DeviceCount--;
            }
        }
    }
    private async Task<string> AccessTokenAsync(PhoneCompanionState state, Dictionary<string, string> keys, CancellationToken ct)
    {
        if (runtime.TokenReference == state.VaultReference && runtime.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2) && runtime.AccessToken is { } cached) return cached;
        using var service = JsonDocument.Parse(keys["firebaseServiceAccount"]);
        var root = service.RootElement;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var body = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { iss = root.GetProperty("client_email").GetString(),
            scope = "https://www.googleapis.com/auth/datastore https://www.googleapis.com/auth/firebase.messaging",
            aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600 }));
        using var rsa = RSA.Create(); rsa.ImportFromPem(root.GetProperty("private_key").GetString()!);
        var signed = header + "." + body;
        var assertion = signed + "." + WebEncoders.Base64UrlEncode(rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion }) };
        using var response = await clients.CreateClient("PhoneFirebase").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Firebase service-account sign-in failed ({(int)response.StatusCode}). Choose a valid key file on the PC.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        runtime.AccessToken = json.RootElement.GetProperty("access_token").GetString()!;
        runtime.TokenReference = state.VaultReference;
        runtime.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
        return runtime.AccessToken;
    }
    private static string DocumentUrl(PhoneCompanionState state, Dictionary<string, string> keys) =>
        $"https://firestore.googleapis.com/v1/projects/{state.Firebase.ProjectId}/databases/(default)/documents/phonePairings/{keys["pairingId"]}";
    private static object StringValue(string value) => new { stringValue = value };
    private async Task<JsonElement> CloudAsync(HttpMethod method, string url, object? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await clients.CreateClient("PhoneFirebase").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) runtime.TokenExpiresAt = DateTimeOffset.MinValue;
            throw new PhoneCloudException(response.StatusCode);
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return json.RootElement.Clone();
    }
}

public sealed class PhoneCloudException(HttpStatusCode status) : InvalidOperationException(
    $"Firebase request failed ({(int)status}). Check Firestore database creation, Cloud Messaging API, and service-account permissions.")
{ public HttpStatusCode Status { get; } = status; }

public sealed class PhoneCompanionWorker(IServiceScopeFactory scopes, ILogger<PhoneCompanionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PhoneCompanionService>().SyncAsync(false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Phone schedule sync failed ({ErrorType}); will retry.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
