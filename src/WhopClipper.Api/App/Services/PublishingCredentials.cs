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

    public async Task<IReadOnlyList<PlatformConnectionDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.PlatformConnections.AsNoTracking().ToDictionaryAsync(x => x.Platform, cancellationToken);
        return PlatformPublishingCatalog.Platforms.Select(platform =>
            ToDto(platform, rows.GetValueOrDefault(platform))).ToList();
    }

    public async Task<PlatformConnectionDto> GetAsync(PlatformKind platform, CancellationToken cancellationToken)
    {
        var row = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Platform == platform, cancellationToken);
        return ToDto(platform, row);
    }

    public async Task<(PlatformConnectionDto? Connection, Dictionary<string, string[]>? Errors)> SaveAsync(
        PlatformKind platform, SavePlatformConnectionRequest request, CancellationToken cancellationToken)
    {
        var errors = Validate(platform, request);
        if (errors.Count > 0) return (null, errors);
        var row = await db.PlatformConnections.SingleOrDefaultAsync(x => x.Platform == platform, cancellationToken);
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

    public async Task<bool> DeleteAsync(PlatformKind platform, CancellationToken cancellationToken)
    {
        var row = await db.PlatformConnections.SingleOrDefaultAsync(x => x.Platform == platform, cancellationToken);
        if (row is null) return false;
        var reference = row.CredentialReference;
        db.PlatformConnections.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        vault.Delete(reference);
        return true;
    }

    public async Task<PlatformConnectionDto> VerifyAsync(PlatformKind platform, CancellationToken cancellationToken)
    {
        var row = await db.PlatformConnections.SingleOrDefaultAsync(x => x.Platform == platform, cancellationToken);
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

    public async Task<Dictionary<string, string>?> GetCredentialsAsync(PlatformKind platform, CancellationToken cancellationToken)
    {
        var row = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Platform == platform, cancellationToken);
        return row is null ? null : ReadCredentials(row);
    }

    public async Task SaveCredentialSetAsync(PlatformKind platform, IReadOnlyDictionary<string, string> credentials,
        DateTimeOffset? tokenExpiresAt, CancellationToken cancellationToken)
    {
        var row = await db.PlatformConnections.SingleOrDefaultAsync(x => x.Platform == platform, cancellationToken)
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
            CleanError(row?.LastError), canPublish, string.IsNullOrWhiteSpace(row?.LastError) ? capability : CleanError(row.LastError)!);
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

    public YouTubeOAuthState Create(string redirectUri)
    {
        Prune();
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var record = new YouTubeOAuthState(state, verifier, redirectUri,
            DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.Value.OAuthStateMinutes, 2, 30)));
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

public sealed record YouTubeOAuthState(string State, string CodeVerifier, string RedirectUri, DateTimeOffset ExpiresAt)
{
    public string CodeChallenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)));
}
