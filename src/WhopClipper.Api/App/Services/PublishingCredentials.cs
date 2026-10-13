using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed class PublishingApiOptions
{
    public string GoogleAuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string GoogleTokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string YouTubeUploadEndpoint { get; set; } = "https://www.googleapis.com/upload/youtube/v3/videos";
    public string YouTubeApiEndpoint { get; set; } = "https://www.googleapis.com/youtube/v3";
    public string InstagramApiBaseUrl { get; set; } = "https://graph.facebook.com";
    public string InstagramApiVersion { get; set; } = "v26.0";
    public int OAuthStateMinutes { get; set; } = 10;
    public int InstagramProcessingTimeoutMinutes { get; set; } = 10;
}

public interface IPublishingCredentialVault
{
    string Store(PlatformKind platform, IReadOnlyDictionary<string, string> credentials);
    Dictionary<string, string> Read(string reference);
    void Delete(string reference);
}

public sealed class PublishingCredentialVault(IDataProtectionProvider provider) : IPublishingCredentialVault
{
    private readonly IDataProtector _protector = provider.CreateProtector("WhopClipper.PlatformOAuthCredentials.v1");
    private static readonly string VaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhopClipper", "secrets");

    public string Store(PlatformKind platform, IReadOnlyDictionary<string, string> credentials)
    {
        Directory.CreateDirectory(VaultRoot);
        var reference = $"publishing-{platform}-{Guid.NewGuid():N}.vault";
        var path = Resolve(reference);
        var temporary = Path.Combine(VaultRoot, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var protectedValue = _protector.Protect(JsonSerializer.Serialize(credentials, JsonDefaults.Serializer));
            File.WriteAllText(temporary, protectedValue, Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
            return reference;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Dictionary<string, string> Read(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return new(StringComparer.OrdinalIgnoreCase);
        var protectedValue = File.ReadAllText(Resolve(reference), Encoding.UTF8);
        var json = _protector.Unprotect(protectedValue);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Serializer)
            ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public void Delete(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        var path = Resolve(reference);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string Resolve(string reference)
    {
        if (Path.GetFileName(reference) != reference) throw new InvalidOperationException("Invalid credential reference.");
        var path = Path.GetFullPath(reference, VaultRoot);
        WorkspaceService.EnsureInside(VaultRoot, path);
        var current = new DirectoryInfo(VaultRoot);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Credential vault cannot use a reparse-point directory.");
            current = current.Parent;
        }
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Credential vault file cannot be a reparse point.");
        return path;
    }
}

public interface IPlatformConnectionVerifier
{
    PlatformKind Platform { get; }
    Task<(bool Valid, string? AccountIdentifier, string? Error)> VerifyAsync(
        IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken);
}

public sealed class PlatformConnectionService(AppDbContext db, IPublishingCredentialVault vault,
    IEnumerable<IPlatformConnectionVerifier> verifiers)
{
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
        { "clientId", "clientSecret", "accessToken", "refreshToken", "accountId", "pageId", "authMode" };
    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
        { "clientSecret", "accessToken", "refreshToken" };

    public const string ChoosePage = "This campaign has no YouTube page chosen yet. Pick one under Review & export, beside Rendered clips.";

    /// <summary>A platform's pages, oldest first. Only YouTube has more than one.</summary>
    private IQueryable<PlatformConnection> Pages(PlatformKind platform) =>
        db.PlatformConnections.Where(x => x.Platform == platform).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

    /// <summary>The page asked for, or the platform's first one when none is named.</summary>
    private IQueryable<PlatformConnection> Page(PlatformKind platform, Guid? connectionId) =>
        connectionId is { } id ? db.PlatformConnections.Where(x => x.Platform == platform && x.Id == id) : Pages(platform);

    /// <summary>Every page of every platform, with a not-configured entry for a platform that has none.</summary>
    public async Task<IReadOnlyList<PlatformConnectionDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.PlatformConnections.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return PlatformPublishingCatalog.Platforms.SelectMany(platform =>
        {
            var pages = rows.Where(x => x.Platform == platform).ToList();
            return pages.Count == 0 ? [ToDto(platform, null)] : pages.Select(x => ToDto(platform, x));
        }).ToList();
    }

    public Task<PlatformConnectionDto> GetAsync(PlatformKind platform, CancellationToken cancellationToken) =>
        GetAsync(platform, null, cancellationToken);

    public async Task<PlatformConnectionDto> GetAsync(PlatformKind platform, Guid? connectionId, CancellationToken cancellationToken)
    {
        var row = await Page(platform, connectionId).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return ToDto(platform, row);
    }

    /// <summary>
    /// The YouTube page a campaign posts to: the one chosen for it, or the only page there is. With several
    /// pages and none chosen there is no safe guess, because a wrong one posts to the wrong channel.
    /// </summary>
    public async Task<(Guid? PageId, bool MustChoose)> ResolveYouTubePageAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var pages = await Pages(PlatformKind.YouTubeShorts).AsNoTracking().Select(x => x.Id).ToListAsync(cancellationToken);
        var chosen = await db.Campaigns.AsNoTracking().Where(x => x.Id == campaignId)
            .Select(x => x.YouTubeConnectionId).SingleOrDefaultAsync(cancellationToken);
        if (chosen is { } id && pages.Contains(id)) return (id, false);
        return pages.Count == 1 ? (pages[0], false) : (null, pages.Count > 1);
    }

    /// <summary>Points a campaign at one of the YouTube pages, or clears the choice with null.</summary>
    public async Task<(bool Found, string? Error)> SetCampaignYouTubePageAsync(Guid campaignId, Guid? pageId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken);
        if (campaign is null) return (false, null);
        if (pageId is { } id && !await db.PlatformConnections.AnyAsync(x => x.Id == id && x.Platform == PlatformKind.YouTubeShorts, cancellationToken))
            return (true, "That YouTube page is no longer listed. Reload and pick again.");
        // Clips already waiting keep the page they were scheduled on; only new ones follow the change.
        campaign.YouTubeConnectionId = pageId;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    /// <summary>
    /// Adds another YouTube page. It starts with the same Google client as an existing page, so the only
    /// thing left is to sign in and pick the channel.
    /// </summary>
    public async Task<(PlatformConnectionDto? Connection, Dictionary<string, string[]>? Errors)> AddYouTubePageAsync(
        string? accountId, Guid? copyClientFrom, CancellationToken cancellationToken)
    {
        var label = accountId?.Trim();
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200 || label.Any(char.IsControl))
            return (null, new() { ["accountId"] = ["Type the new page's channel handle, for example @yourchannel."] });
        var existing = await Pages(PlatformKind.YouTubeShorts).AsNoTracking().ToListAsync(cancellationToken);
        if (existing.Any(x => string.Equals(x.AccountIdentifier, label, StringComparison.OrdinalIgnoreCase)))
            return (null, new() { ["accountId"] = ["A page with that handle is already listed."] });
        var source = copyClientFrom is { } from ? existing.FirstOrDefault(x => x.Id == from) : existing.FirstOrDefault();
        var credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["accountId"] = label };
        var client = ReadCredentials(source);
        foreach (var key in new[] { "clientId", "clientSecret" })
            if (client.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) credentials[key] = value;
        var row = new PlatformConnection { Platform = PlatformKind.YouTubeShorts };
        Apply(row, credentials);
        db.PlatformConnections.Add(row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { vault.Delete(row.CredentialReference); throw; }
        return (ToDto(PlatformKind.YouTubeShorts, row, credentials), null);
    }

    private void Apply(PlatformConnection row, IReadOnlyDictionary<string, string> credentials)
    {
        row.CredentialReference = vault.Store(row.Platform, credentials);
        row.ConfiguredFieldsJson = JsonDefaults.StringArray(credentials.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Key));
        row.SecretFlagsJson = JsonSerializer.Serialize(SecretFields.ToDictionary(x => x,
            x => credentials.TryGetValue(x, out var value) && !string.IsNullOrWhiteSpace(value)), JsonDefaults.Serializer);
        row.AccountIdentifier = credentials.GetValueOrDefault("accountId");
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Task<(PlatformConnectionDto? Connection, Dictionary<string, string[]>? Errors)> SaveAsync(
        PlatformKind platform, SavePlatformConnectionRequest request, CancellationToken cancellationToken) =>
        SaveAsync(platform, null, request, cancellationToken);

    public async Task<(PlatformConnectionDto? Connection, Dictionary<string, string[]>? Errors)> SaveAsync(
        PlatformKind platform, Guid? connectionId, SavePlatformConnectionRequest request, CancellationToken cancellationToken)
    {
        var errors = Validate(platform, request);
        if (errors.Count > 0) return (null, errors);
        var row = await Page(platform, connectionId).FirstOrDefaultAsync(cancellationToken);
        if (row is null && connectionId is not null)
            return (null, new() { ["connectionId"] = ["That page is no longer listed. Reload and try again."] });
        var credentials = ReadCredentials(row);
        foreach (var field in request.ClearFields ?? []) credentials.Remove(field);
        Set(credentials, "clientId", request.ClientId);
        Set(credentials, "clientSecret", request.ClientSecret);
        Set(credentials, "accessToken", request.AccessToken);
        Set(credentials, "refreshToken", request.RefreshToken);
        Set(credentials, "accountId", request.AccountId);
        Set(credentials, "pageId", request.PageId);
        Set(credentials, "authMode", request.AuthMode);
        if (row is null)
        {
            row = new PlatformConnection { Platform = platform };
            db.PlatformConnections.Add(row);
        }
        var oldReference = row.CredentialReference;
        var newReference = vault.Store(platform, credentials);
        row.CredentialReference = newReference;
        row.ConfiguredFieldsJson = JsonDefaults.StringArray(credentials.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Key));
        row.SecretFlagsJson = JsonSerializer.Serialize(SecretFields.ToDictionary(x => x,
            x => credentials.TryGetValue(x, out var value) && !string.IsNullOrWhiteSpace(value)), JsonDefaults.Serializer);
        row.AccountIdentifier = credentials.GetValueOrDefault("accountId");
        row.TokenExpiresAt = request.TokenExpiresAt ?? (request.ClearFields?.Contains("tokenExpiresAt", StringComparer.OrdinalIgnoreCase) == true
            ? null : row.TokenExpiresAt);
        row.LastError = null;
        row.LastValidatedAt = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { vault.Delete(newReference); throw; }
        if (!string.IsNullOrWhiteSpace(oldReference) && !oldReference.Equals(newReference, StringComparison.Ordinal)) vault.Delete(oldReference);
        return (ToDto(platform, row, credentials), null);
    }

    /// <summary>A YouTube page with its stored sign-in, for checks that span pages.</summary>
    public sealed record YouTubePage(Guid Id, string Label, Dictionary<string, string> Credentials, DateTimeOffset? TokenExpiresAt);

    public async Task<IReadOnlyList<YouTubePage>> YouTubePagesAsync(CancellationToken cancellationToken) =>
        (await Pages(PlatformKind.YouTubeShorts).AsNoTracking().ToListAsync(cancellationToken)).Select(x =>
        {
            var credentials = ReadCredentials(x);
            return new YouTubePage(x.Id, PageLabel(x.AccountIdentifier, credentials.GetValueOrDefault("channelTitle")), credentials, x.TokenExpiresAt);
        }).ToList();

    /// <summary>The handle typed for a page, with the channel name YouTube reports when the two differ.</summary>
    public static string PageLabel(string? typed, string? channelTitle)
    {
        var label = string.IsNullOrWhiteSpace(typed) ? "Unnamed page" : typed.Trim();
        return string.IsNullOrWhiteSpace(channelTitle) || string.Equals(channelTitle, label.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
            ? label : $"{label} ({channelTitle})";
    }

    /// <summary>States in which a clip still depends on its page: waiting, uploading, or uploaded and not yet public.</summary>
    private static readonly PublishStatus[] StillNeedsPage =
        [PublishStatus.Queued, PublishStatus.Validating, PublishStatus.Uploading, PublishStatus.Processing, PublishStatus.Scheduled];

    /// <returns>Whether the page existed, and why it could not be removed when it could not.</returns>
    public async Task<(bool Found, string? Blocked)> DeleteAsync(PlatformKind platform, Guid? connectionId, CancellationToken cancellationToken)
    {
        var row = await Page(platform, connectionId).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return (false, null);
        // Removing the sign-in would strand these: a waiting clip could not upload, and an uploaded one
        // could no longer be taken off or moved.
        var waiting = await db.PublishJobs.CountAsync(x => x.ConnectionId == row.Id && x.ScheduledPublishAt != null
            && StillNeedsPage.Contains(x.Status), cancellationToken);
        if (waiting > 0)
            return (true, $"{waiting} clip{(waiting == 1 ? " is" : "s are")} still on this page's schedule. Take {(waiting == 1 ? "it" : "them")} off YouTube first, or wait until {(waiting == 1 ? "it has" : "they have")} gone public.");
        var reference = row.CredentialReference;
        foreach (var campaign in await db.Campaigns.Where(x => x.YouTubeConnectionId == row.Id).ToListAsync(cancellationToken))
            campaign.YouTubeConnectionId = null;
        db.PlatformConnections.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        vault.Delete(reference);
        return (true, null);
    }

    public Task<PlatformConnectionDto> VerifyAsync(PlatformKind platform, CancellationToken cancellationToken) =>
        VerifyAsync(platform, null, cancellationToken);

    public async Task<PlatformConnectionDto> VerifyAsync(PlatformKind platform, Guid? connectionId, CancellationToken cancellationToken)
    {
        var row = await Page(platform, connectionId).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return ToDto(platform, null);
        var credentials = ReadCredentials(row);
        var readiness = Readiness(platform, credentials, row.TokenExpiresAt, row.LastError);
        if (!readiness.CanPublish)
        {
            row.LastValidatedAt = DateTimeOffset.UtcNow;
            row.LastError = readiness.Message;
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(platform, row, credentials);
        }
        var verifier = verifiers.SingleOrDefault(x => x.Platform == platform);
        if (verifier is null)
        {
            row.LastValidatedAt = DateTimeOffset.UtcNow;
            row.LastError = "This platform is manual-only.";
        }
        else
        {
            var result = await verifier.VerifyAsync(credentials, cancellationToken);
            row.LastValidatedAt = DateTimeOffset.UtcNow;
            row.LastError = result.Valid ? null : CleanError(result.Error);
            if (!string.IsNullOrWhiteSpace(result.AccountIdentifier)) row.AccountIdentifier = result.AccountIdentifier;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(platform, row, credentials);
    }

    public Task<Dictionary<string, string>?> GetCredentialsAsync(PlatformKind platform, CancellationToken cancellationToken) =>
        GetCredentialsAsync(platform, null, cancellationToken);

    public async Task<Dictionary<string, string>?> GetCredentialsAsync(PlatformKind platform, Guid? connectionId, CancellationToken cancellationToken)
    {
        var row = await Page(platform, connectionId).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ReadCredentials(row);
    }

    public Task SaveCredentialSetAsync(PlatformKind platform, IReadOnlyDictionary<string, string> credentials,
        DateTimeOffset? tokenExpiresAt, CancellationToken cancellationToken) =>
        SaveCredentialSetAsync(platform, null, credentials, tokenExpiresAt, cancellationToken);

    public async Task SaveCredentialSetAsync(PlatformKind platform, Guid? connectionId, IReadOnlyDictionary<string, string> credentials,
        DateTimeOffset? tokenExpiresAt, CancellationToken cancellationToken)
    {
        var row = await Page(platform, connectionId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Platform connection no longer exists.");
        var oldReference = row.CredentialReference;
        var newReference = vault.Store(platform, credentials);
        row.CredentialReference = newReference;
        row.ConfiguredFieldsJson = JsonDefaults.StringArray(credentials.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Key));
        row.SecretFlagsJson = JsonSerializer.Serialize(SecretFields.ToDictionary(x => x,
            x => credentials.TryGetValue(x, out var value) && !string.IsNullOrWhiteSpace(value)), JsonDefaults.Serializer);
        row.AccountIdentifier = credentials.GetValueOrDefault("accountId");
        row.GrantedScopesJson = JsonDefaults.StringArray(ParseScopes(credentials.GetValueOrDefault("grantedScopes")));
        row.TokenExpiresAt = tokenExpiresAt;
        row.LastError = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { vault.Delete(newReference); throw; }
        if (!string.IsNullOrWhiteSpace(oldReference) && !oldReference.Equals(newReference, StringComparison.Ordinal)) vault.Delete(oldReference);
    }

    internal PlatformConnectionDto ToDto(PlatformKind platform, PlatformConnection? row,
        IReadOnlyDictionary<string, string>? knownCredentials = null)
    {
        var credentials = knownCredentials ?? (row is null ? new Dictionary<string, string>() : ReadCredentials(row));
        var readiness = Readiness(platform, credentials, row?.TokenExpiresAt, row?.LastError);
        var status = row is null || credentials.Count == 0 ? PublishingConnectionStatus.NotConfigured
            : readiness.CanPublish && string.IsNullOrWhiteSpace(row.LastError) ? PublishingConnectionStatus.Configured
            : PublishingConnectionStatus.NeedsAttention;
        var verified = row?.LastValidatedAt is not null && string.IsNullOrWhiteSpace(row.LastError);
        var canPublish = readiness.CanPublish && verified;
        var capability = !readiness.CanPublish ? readiness.Message : !verified
            ? "Credentials are saved. Validate this connection before publishing." : readiness.Message;
        return new(platform, PlatformPublishingCatalog.Mode(platform), status,
            credentials.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Key).Order().ToArray(),
            SecretFields.ToDictionary(x => x, x => credentials.TryGetValue(x, out var value) && !string.IsNullOrWhiteSpace(value), StringComparer.OrdinalIgnoreCase),
            row?.AccountIdentifier, row is null ? [] : JsonDefaults.Parse(row.GrantedScopesJson, Array.Empty<string>()),
            row?.TokenExpiresAt, row?.UpdatedAt, row?.LastValidatedAt,
            CleanError(row?.LastError), canPublish, string.IsNullOrWhiteSpace(row?.LastError) ? capability : CleanError(row.LastError)!,
            row?.Id, credentials.GetValueOrDefault("channelTitle"));
    }

    internal static (bool CanPublish, string Message) Readiness(PlatformKind platform,
        IReadOnlyDictionary<string, string> credentials, DateTimeOffset? expiresAt, string? lastError)
    {
        if (platform == PlatformKind.TikTok) return (false, "TikTok publishing is manual; download the prepared package.");
        if (platform == PlatformKind.YouTubeShorts)
        {
            if (!Has(credentials, "clientId"))
                return (false, "Save a Google Desktop OAuth client ID first.");
            if (!Has(credentials, "accountId"))
                return (false, "Save the exact YouTube channel handle or channel ID so every upload destination can be reviewed.");
            if (!Has(credentials, "refreshToken") && !Has(credentials, "accessToken"))
                return (false, "Connect YouTube in the browser or paste an OAuth token.");
            if (!Has(credentials, "refreshToken") && expiresAt.HasValue && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
                return (false, "The YouTube access token expired; reconnect YouTube.");
            return (true, "YouTube resumable upload is configured. New uploads default to private.");
        }
        if (platform == PlatformKind.InstagramReels)
        {
            if (!Has(credentials, "accessToken") || !Has(credentials, "accountId") || !Has(credentials, "pageId") ||
                !string.Equals(credentials.GetValueOrDefault("authMode"), "FacebookLoginForBusiness", StringComparison.OrdinalIgnoreCase))
                return (false, "Local Instagram upload requires a Page-linked Professional account, Page access token, Page ID, IG account ID, and FacebookLoginForBusiness mode.");
            if (expiresAt.HasValue && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
                return (false, "The Instagram access token is expired; replace it before publishing.");
            return (true, "Instagram local resumable upload prerequisites are configured; validate before publishing.");
        }
        return (false, "This platform is not supported for publishing.");
    }

    internal Dictionary<string, string> ReadCredentials(PlatformConnection? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.CredentialReference)) return new(StringComparer.OrdinalIgnoreCase);
        try { return new(vault.Read(row.CredentialReference), StringComparer.OrdinalIgnoreCase); }
        catch (CryptographicException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static Dictionary<string, string[]> Validate(PlatformKind platform, SavePlatformConnectionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!PlatformPublishingCatalog.IsPublishingPlatform(platform)) errors["platform"] = ["Unsupported publishing platform."];
        if (platform == PlatformKind.TikTok) errors["platform"] = ["TikTok is manual-only. Whop Clipper never accepts TikTok login credentials."];
        foreach (var unknown in request.AdditionalFields?.Keys ?? Enumerable.Empty<string>())
            errors[unknown] = [IsForbiddenLoginField(unknown)
                ? "Social passwords, passcodes, 2FA/OTP codes, recovery codes, and security answers are never accepted."
                : "Unknown credential field."];
        foreach (var field in request.ClearFields ?? []) if (!KnownFields.Contains(field) && !field.Equals("tokenExpiresAt", StringComparison.OrdinalIgnoreCase))
            errors["clearFields"] = ["clearFields contains an unsupported name."];
        foreach (var pair in new Dictionary<string, string?>
        {
            ["clientId"] = request.ClientId, ["clientSecret"] = request.ClientSecret,
            ["accessToken"] = request.AccessToken, ["refreshToken"] = request.RefreshToken,
            ["accountId"] = request.AccountId, ["pageId"] = request.PageId, ["authMode"] = request.AuthMode
        })
            if (pair.Value is { Length: > 16_384 } || pair.Value?.Any(char.IsControl) == true)
                errors[pair.Key] = ["Credential value is too long or contains control characters."];
        if (!string.IsNullOrWhiteSpace(request.AuthMode) && !request.AuthMode.Equals("FacebookLoginForBusiness", StringComparison.Ordinal))
            errors["authMode"] = ["The only local Instagram upload mode is FacebookLoginForBusiness."];
        return errors;
    }

    private static bool IsForbiddenLoginField(string field)
    {
        var normalized = new string(field.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return new[] { "password", "passcode", "2fa", "twofactor", "otp", "recovery", "backupcode", "securityanswer" }
            .Any(normalized.Contains);
    }

    private static void Set(IDictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value.Trim();
    }
    private static bool Has(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    private static string[] ParseScopes(string? scopes) => string.IsNullOrWhiteSpace(scopes)
        ? []
        : scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal static string? CleanError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var clean = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var marker in new[] { "authorization", "bearer", "oauth", "access_token", "refresh_token", "client_secret", "token", "secret" })
        {
            var index = clean.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) clean = clean[..Math.Min(clean.Length, index + marker.Length)] + " [redacted]";
        }
        return clean[..Math.Min(clean.Length, 500)];
    }
}

public sealed class LocalPublishingRequestGuard
{
    public const string HeaderName = "X-WhopClipper-Nonce";
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    public string Nonce => WebEncoders.Base64UrlEncode(_nonce);

    public bool IsValid(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        byte[] candidate;
        try { candidate = WebEncoders.Base64UrlDecode(supplied); }
        catch (FormatException) { return false; }
        return candidate.Length == _nonce.Length && CryptographicOperations.FixedTimeEquals(candidate, _nonce);
    }
}

public sealed class YouTubeOAuthStateStore(IOptions<PublishingApiOptions> options)
{
    private readonly ConcurrentDictionary<string, YouTubeOAuthState> _states = new(StringComparer.Ordinal);

    public YouTubeOAuthState Create(string redirectUri, Guid? connectionId = null)
    {
        Prune();
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var record = new YouTubeOAuthState(state, verifier, redirectUri,
            DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.Value.OAuthStateMinutes, 2, 30)), connectionId);
        _states[state] = record;
        return record;
    }

    public YouTubeOAuthState? Consume(string state)
    {
        if (!_states.TryRemove(state, out var record) || record.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return record;
    }

    internal void AddForTest(YouTubeOAuthState state) => _states[state.State] = state;
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _states.Where(x => x.Value.ExpiresAt <= now)) _states.TryRemove(pair.Key, out _);
    }
}

/// <param name="ConnectionId">The YouTube page this sign-in is for. Null is the first page.</param>
public sealed record YouTubeOAuthState(string State, string CodeVerifier, string RedirectUri, DateTimeOffset ExpiresAt,
    Guid? ConnectionId = null)
{
    public string CodeChallenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)));
}
