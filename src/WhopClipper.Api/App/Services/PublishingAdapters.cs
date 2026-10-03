using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <param name="PublishAt">YouTube only: upload private and let YouTube make it public at this time.</param>
public sealed record PlatformPublishRequest(Guid JobId, string FilePath, string Title, string Caption,
    string Privacy, bool ShareToFeed, bool DryRun, DateTimeOffset? PublishAt = null);
public sealed record PlatformPublishResult(string ExternalId, string? ExternalUrl, string ResultCode = "Published");
public sealed record InstagramPreparedResult(string ContainerId);

public interface IPlatformPublisher
{
    PlatformKind Platform { get; }
    Task<PlatformPublishResult> PublishAsync(PlatformPublishRequest request, Dictionary<string, string> credentials,
        IProgress<double> progress, CancellationToken cancellationToken);
}

public sealed class PublishingApiException(string safeMessage, HttpStatusCode? statusCode = null) : Exception(safeMessage)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class YouTubePublisher(IHttpClientFactory clients, IOptions<PublishingApiOptions> options)
    : IPlatformPublisher, IPlatformConnectionVerifier
{
    public PlatformKind Platform => PlatformKind.YouTubeShorts;

    public async Task<PlatformPublishResult> PublishAsync(PlatformPublishRequest request,
        Dictionary<string, string> credentials, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (request.DryRun)
        {
            progress.Report(1);
            return new($"dry-run-{request.JobId:N}", null, "DryRun");
        }
        var token = await GetValidAccessTokenAsync(credentials, cancellationToken);
        var file = new FileInfo(request.FilePath);
        var client = clients.CreateClient("PublishingExternal");
        var endpoint = QueryHelpers.AddQueryString(options.Value.YouTubeUploadEndpoint,
            new Dictionary<string, string?> { ["uploadType"] = "resumable", ["part"] = "snippet,status" });
        // A scheduled upload stays private until publishAt; YouTube flips it public on its own, so
        // the slot is met even when this machine is off.
        object status = request.PublishAt is { } publishAt
            ? new { privacyStatus = "private", publishAt = FormatPublishAt(publishAt), selfDeclaredMadeForKids = false }
            : new { privacyStatus = request.Privacy };
        var metadata = JsonSerializer.Serialize(new
        {
            snippet = new { title = request.Title, description = request.Caption, categoryId = "22" },
            status
        }, JsonDefaults.Serializer);
        using var initiate = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(metadata, Encoding.UTF8, "application/json")
        };
        initiate.Headers.Authorization = new("Bearer", token);
        initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Length", file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/mp4");
        using var initiated = await client.SendAsync(initiate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!initiated.IsSuccessStatusCode) throw await SafeFailureAsync("YouTube could not start the resumable upload", initiated, cancellationToken);
        var uploadUri = initiated.Headers.Location ?? throw new PublishingApiException("YouTube did not return a resumable upload URL.");
        if (uploadUri.Scheme != Uri.UriSchemeHttps || !IsHostOrSubdomain(uploadUri.Host, "googleapis.com"))
            throw new PublishingApiException("YouTube returned an unsafe upload URL.");

        await using var stream = new FileStream(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var upload = new HttpRequestMessage(HttpMethod.Put, uploadUri)
        {
            Content = new ProgressStreamContent(stream, file.Length, "video/mp4", value => progress.Report(0.1 + value * 0.85))
        };
        upload.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await SafeFailureAsync("YouTube upload failed", response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var id = document.RootElement.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) throw new PublishingApiException("YouTube accepted the upload but returned no video ID.");
        progress.Report(1);
        return new(id, $"https://youtu.be/{Uri.EscapeDataString(id)}");
    }

    internal static string FormatPublishAt(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Moves a scheduled (still private, never published) video to a new go-public time. videos.update
    /// replaces the whole status part, so the current status is read first and only publishAt changes.
    /// Needs the youtube scope; youtube.upload alone cannot update.
    /// </summary>
    public async Task RescheduleAsync(string videoId, DateTimeOffset publishAt, Dictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        var token = await GetValidAccessTokenAsync(credentials, cancellationToken);
        var client = clients.CreateClient("PublishingExternal");
        using var read = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(
            $"{options.Value.YouTubeApiEndpoint.TrimEnd('/')}/videos", new Dictionary<string, string?> { ["part"] = "status", ["id"] = videoId }));
        read.Headers.Authorization = new("Bearer", token);
        using var current = await client.SendAsync(read, cancellationToken);
        if (!current.IsSuccessStatusCode) throw await SafeFailureAsync("YouTube could not read the scheduled video", current, cancellationToken);
        var root = System.Text.Json.Nodes.JsonNode.Parse(await current.Content.ReadAsStringAsync(cancellationToken));
        var status = root?["items"]?.AsArray().FirstOrDefault()?["status"]?.AsObject()
            ?? throw new PublishingApiException("YouTube no longer has this video. It may have been deleted in YouTube Studio.");
        if (!string.Equals(status["privacyStatus"]?.GetValue<string>(), "private", StringComparison.OrdinalIgnoreCase))
            throw new PublishingApiException("This video is already public, so its go-public time cannot change.");
        var updated = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in status)
            if (key is "privacyStatus" or "embeddable" or "license" or "publicStatsViewable" or "selfDeclaredMadeForKids" or "containsSyntheticMedia")
                updated[key] = value?.DeepClone();
        updated["privacyStatus"] = "private";
        updated["publishAt"] = FormatPublishAt(publishAt);
        using var write = new HttpRequestMessage(HttpMethod.Put, QueryHelpers.AddQueryString(
            $"{options.Value.YouTubeApiEndpoint.TrimEnd('/')}/videos", new Dictionary<string, string?> { ["part"] = "status" }))
        {
            Content = new StringContent(new System.Text.Json.Nodes.JsonObject { ["id"] = videoId, ["status"] = updated }.ToJsonString(),
                Encoding.UTF8, "application/json")
        };
        write.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(write, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await SafeFailureAsync("YouTube could not move the scheduled video", response, cancellationToken);
    }

    /// <summary>Deletes an uploaded video. A video already gone counts as deleted.</summary>
    public async Task DeleteVideoAsync(string videoId, Dictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        var token = await GetValidAccessTokenAsync(credentials, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, QueryHelpers.AddQueryString(
            $"{options.Value.YouTubeApiEndpoint.TrimEnd('/')}/videos", new Dictionary<string, string?> { ["id"] = videoId }));
        request.Headers.Authorization = new("Bearer", token);
        using var response = await clients.CreateClient("PublishingExternal").SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await SafeFailureAsync("YouTube could not delete the video", response, cancellationToken);
    }

    public async Task<(bool Valid, string? AccountIdentifier, string? Error)> VerifyAsync(
        IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        try
        {
            var mutable = new Dictionary<string, string>(credentials, StringComparer.OrdinalIgnoreCase);
            _ = await GetValidAccessTokenAsync(mutable, cancellationToken);
            return (true, credentials.GetValueOrDefault("accountId"), null);
        }
        catch (PublishingApiException ex) { return (false, null, ex.Message); }
    }

    internal async Task<string> GetValidAccessTokenAsync(Dictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        if (credentials.TryGetValue("accessToken", out var access) && !string.IsNullOrWhiteSpace(access) &&
            (!credentials.TryGetValue("expiresAt", out var expiresRaw) || !DateTimeOffset.TryParse(expiresRaw, out var expires) ||
             expires > DateTimeOffset.UtcNow.AddMinutes(1))) return access;
        if (!credentials.TryGetValue("refreshToken", out var refresh) || string.IsNullOrWhiteSpace(refresh) ||
            !credentials.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId))
            throw new PublishingApiException("YouTube authorization expired; reconnect YouTube.");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["refresh_token"] = refresh, ["grant_type"] = "refresh_token"
        };
        if (credentials.TryGetValue("clientSecret", out var secret) && !string.IsNullOrWhiteSpace(secret)) form["client_secret"] = secret;
        using var response = await clients.CreateClient("PublishingExternal").PostAsync(options.Value.GoogleTokenEndpoint,
            new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode) throw await SafeFailureAsync("YouTube OAuth token refresh failed", response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        access = json.RootElement.TryGetProperty("access_token", out var node) ? node.GetString() : null;
        if (string.IsNullOrWhiteSpace(access)) throw new PublishingApiException("YouTube OAuth returned no access token.");
        credentials["accessToken"] = access;
        var seconds = json.RootElement.TryGetProperty("expires_in", out var secondsNode) && secondsNode.TryGetInt32(out var parsed) ? parsed : 3600;
        credentials["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("O");
        return access;
    }

    internal static async Task<PublishingApiException> SafeFailureAsync(string prefix, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var message = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
                message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested)
                    ? nested.GetString() ?? string.Empty : error.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        message = PlatformConnectionService.CleanError(new string(message.Where(x => !char.IsControl(x)).ToArray())) ?? string.Empty;
        if (message.Length > 300) message = message[..300];
        return new PublishingApiException($"{prefix} ({(int)response.StatusCode}){(message.Length == 0 ? "." : $": {message}")}", response.StatusCode);
    }

    internal static bool IsHostOrSubdomain(string candidate, string trustedHost) =>
        candidate.Equals(trustedHost, StringComparison.OrdinalIgnoreCase) ||
        candidate.EndsWith($".{trustedHost}", StringComparison.OrdinalIgnoreCase);
}

public sealed class InstagramPublisher(IHttpClientFactory clients, IOptions<PublishingApiOptions> options)
    : IPlatformPublisher, IPlatformConnectionVerifier
{
    public PlatformKind Platform => PlatformKind.InstagramReels;

    public async Task<PlatformPublishResult> PublishAsync(PlatformPublishRequest request,
        Dictionary<string, string> credentials, IProgress<double> progress, CancellationToken cancellationToken)
    {
        throw new PublishingApiException("Instagram publishing requires the separate prepare and final-confirmation steps.");
    }

    public async Task<InstagramPreparedResult> PrepareAsync(PlatformPublishRequest request,
        Dictionary<string, string> credentials, IProgress<double> progress, CancellationToken cancellationToken)
    {
        RequireFacebookLoginForBusiness(credentials);
        var token = credentials["accessToken"];
        var accountId = credentials["accountId"];
        var client = clients.CreateClient("PublishingExternal");
        var createUri = Endpoint(accountId, "media");
        using var create = new HttpRequestMessage(HttpMethod.Post, createUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["media_type"] = "REELS", ["upload_type"] = "resumable", ["caption"] = request.Caption,
                ["share_to_feed"] = request.ShareToFeed ? "true" : "false"
            })
        };
        create.Headers.Authorization = new("Bearer", token);
        using var created = await client.SendAsync(create, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!created.IsSuccessStatusCode) throw await YouTubePublisher.SafeFailureAsync("Instagram could not create the Reel container", created, cancellationToken);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        var containerId = ReadRequired(createdJson.RootElement, "id", "Instagram returned no container ID.");
        var uploadUriRaw = ReadRequired(createdJson.RootElement, "uri", "Instagram did not return a local upload URL.");
        if (!Uri.TryCreate(uploadUriRaw, UriKind.Absolute, out var uploadUri) || uploadUri.Scheme != Uri.UriSchemeHttps ||
            !YouTubePublisher.IsHostOrSubdomain(uploadUri.Host, "facebook.com"))
            throw new PublishingApiException("Instagram returned an unsafe upload URL.");

        var file = new FileInfo(request.FilePath);
        await using var stream = new FileStream(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUri)
        {
            Content = new ProgressStreamContent(stream, file.Length, "video/mp4", value => progress.Report(0.1 + value * 0.65))
        };
        upload.Headers.TryAddWithoutValidation("Authorization", $"OAuth {token}");
        upload.Headers.TryAddWithoutValidation("offset", "0");
        upload.Headers.TryAddWithoutValidation("file_size", file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var uploaded = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!uploaded.IsSuccessStatusCode) throw await YouTubePublisher.SafeFailureAsync("Instagram local video upload failed", uploaded, cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.Value.InstagramProcessingTimeoutMinutes, 1, 30));
        var finished = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get,
                QueryHelpers.AddQueryString(Endpoint(containerId), "fields", "status_code,status"));
            statusRequest.Headers.Authorization = new("Bearer", token);
            using var status = await client.SendAsync(statusRequest, cancellationToken);
            if (!status.IsSuccessStatusCode) throw await YouTubePublisher.SafeFailureAsync("Instagram container status check failed", status, cancellationToken);
            using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync(cancellationToken));
            var code = statusJson.RootElement.TryGetProperty("status_code", out var statusNode) ? statusNode.GetString() : null;
            if (string.Equals(code, "FINISHED", StringComparison.OrdinalIgnoreCase)) { finished = true; break; }
            if (code is "ERROR" or "EXPIRED") throw new PublishingApiException($"Instagram container ended with status {code}.");
            progress.Report(0.8);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        if (!finished) throw new PublishingApiException("Instagram did not finish processing before the local timeout. The container was not blindly published.");
        progress.Report(0.9);
        return new(containerId);
    }

    public async Task<PlatformPublishResult> FinalizeAsync(Guid jobId, string containerId,
        IReadOnlyDictionary<string, string> credentials, IProgress<double> progress, CancellationToken cancellationToken)
    {
        RequireFacebookLoginForBusiness(credentials);
        var token = credentials["accessToken"];
        var accountId = credentials["accountId"];
        // Exactly one media_publish call. A timeout or ambiguous response is left failed for human review, never retried automatically.
        using var publish = new HttpRequestMessage(HttpMethod.Post, Endpoint(accountId, "media_publish"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["creation_id"] = containerId })
        };
        publish.Headers.Authorization = new("Bearer", token);
        using var published = await clients.CreateClient("PublishingExternal").SendAsync(publish, cancellationToken);
        if (!published.IsSuccessStatusCode) throw await YouTubePublisher.SafeFailureAsync("Instagram media_publish failed", published, cancellationToken);
        using var publishJson = JsonDocument.Parse(await published.Content.ReadAsStringAsync(cancellationToken));
        var mediaId = ReadRequired(publishJson.RootElement, "id", "Instagram published but returned no media ID.");
        progress.Report(1);
        return new(mediaId, null);
    }

    public async Task<(bool Valid, string? AccountIdentifier, string? Error)> VerifyAsync(
        IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        try
        {
            RequireFacebookLoginForBusiness(credentials);
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get,
                QueryHelpers.AddQueryString(Endpoint(credentials["pageId"]), "fields", "id,instagram_business_account"));
            pageRequest.Headers.Authorization = new("Bearer", credentials["accessToken"]);
            using var pageResponse = await clients.CreateClient("PublishingExternal").SendAsync(pageRequest, cancellationToken);
            if (!pageResponse.IsSuccessStatusCode)
                throw await YouTubePublisher.SafeFailureAsync("Instagram Page-link validation failed", pageResponse, cancellationToken);
            using var pageJson = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync(cancellationToken));
            var linkedId = pageJson.RootElement.TryGetProperty("instagram_business_account", out var linked) &&
                linked.TryGetProperty("id", out var linkedNode) ? linkedNode.GetString() : null;
            if (!string.Equals(linkedId, credentials["accountId"], StringComparison.Ordinal))
                throw new PublishingApiException("The Facebook Page is not linked to the configured Instagram professional account ID.");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                QueryHelpers.AddQueryString(Endpoint(credentials["accountId"]), "fields", "id,username"));
            request.Headers.Authorization = new("Bearer", credentials["accessToken"]);
            using var response = await clients.CreateClient("PublishingExternal").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw await YouTubePublisher.SafeFailureAsync("Instagram connection validation failed", response, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var id = ReadRequired(json.RootElement, "id", "Instagram returned no account ID.");
            return (string.Equals(id, credentials["accountId"], StringComparison.Ordinal), id,
                string.Equals(id, credentials["accountId"], StringComparison.Ordinal) ? null : "Instagram account ID did not match.");
        }
        catch (PublishingApiException ex) { return (false, null, ex.Message); }
    }

    private string Endpoint(string id, string? operation = null)
    {
        var root = options.Value.InstagramApiBaseUrl.TrimEnd('/');
        var version = options.Value.InstagramApiVersion.Trim('/');
        return $"{root}/{version}/{Uri.EscapeDataString(id)}{(operation is null ? "" : $"/{operation}")}";
    }

    internal static void RequireFacebookLoginForBusiness(IReadOnlyDictionary<string, string> credentials)
    {
        if (!credentials.ContainsKey("accessToken") || !credentials.ContainsKey("accountId") || !credentials.ContainsKey("pageId") ||
            !credentials.TryGetValue("authMode", out var mode) || mode != "FacebookLoginForBusiness")
            throw new PublishingApiException("Instagram local upload requires Facebook Login for Business, a linked Page, Page access token, Page ID, and Professional IG account ID.");
    }

    private static string ReadRequired(JsonElement root, string property, string error)
    {
        var value = root.TryGetProperty(property, out var node) ? node.GetString() : null;
        return string.IsNullOrWhiteSpace(value) ? throw new PublishingApiException(error) : value;
    }
}

internal sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream _stream;
    private readonly long _length;
    private readonly Action<double> _report;

    public ProgressStreamContent(Stream stream, long length, string contentType, Action<double> report)
    {
        _stream = stream;
        _length = length;
        _report = report;
        Headers.ContentType = new MediaTypeHeaderValue(contentType);
        Headers.ContentLength = length;
    }

    protected override bool TryComputeLength(out long computedLength) { computedLength = _length; return true; }
    protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
    {
        var buffer = new byte[1024 * 1024];
        long written = 0;
        int read;
        while ((read = await _stream.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            written += read;
            _report(_length == 0 ? 1 : Math.Clamp((double)written / _length, 0, 1));
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);
}

public sealed class YouTubeOAuthService(IHttpClientFactory clients, IOptions<PublishingApiOptions> options,
    YouTubeOAuthStateStore states, PlatformConnectionService connections)
{
    public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    public const string ManageScope = "https://www.googleapis.com/auth/youtube";

    /// <summary>Scheduling moves and deletes videos, which the upload-only scope cannot do.</summary>
    public static bool CanManageSchedule(IEnumerable<string> grantedScopes) =>
        grantedScopes.Contains(ManageScope, StringComparer.Ordinal);

    public async Task<OAuthStartResponse> StartAsync(string redirectUri, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var callback) || callback.Scheme != Uri.UriSchemeHttp ||
            callback.Host != "127.0.0.1" || callback.Port is < 1 or > 65535 ||
            callback.AbsolutePath != "/api/publishing/oauth/youtube/callback")
            throw new InvalidOperationException("The YouTube OAuth callback must use the app's 127.0.0.1 loopback address.");
        var credentials = await connections.GetCredentialsAsync(PlatformKind.YouTubeShorts, cancellationToken);
        if (credentials is null || !credentials.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Save the Google Desktop OAuth client ID before connecting YouTube.");
        var state = states.Create(redirectUri);
        var url = QueryHelpers.AddQueryString(options.Value.GoogleAuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            // Upload, plus the youtube scope that moving a scheduled video to another slot and taking
            // one off (videos.update / videos.delete) require.
            ["scope"] = $"{UploadScope} {ManageScope}", ["access_type"] = "offline",
            ["prompt"] = "consent", ["state"] = state.State, ["code_challenge"] = state.CodeChallenge,
            ["code_challenge_method"] = "S256"
        });
        return new(url, state.ExpiresAt);
    }

    public async Task CompleteAsync(string code, string stateValue, CancellationToken cancellationToken)
    {
        var state = states.Consume(stateValue) ?? throw new InvalidOperationException("OAuth state is invalid, expired, or already used.");
        var credentials = await connections.GetCredentialsAsync(PlatformKind.YouTubeShorts, cancellationToken)
            ?? throw new InvalidOperationException("YouTube OAuth configuration was removed.");
        if (!credentials.TryGetValue("clientId", out var clientId)) throw new InvalidOperationException("YouTube client ID is missing.");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = state.CodeVerifier,
            ["redirect_uri"] = state.RedirectUri, ["grant_type"] = "authorization_code"
        };
        if (credentials.TryGetValue("clientSecret", out var secret) && !string.IsNullOrWhiteSpace(secret)) form["client_secret"] = secret;
        using var response = await clients.CreateClient("PublishingExternal").PostAsync(options.Value.GoogleTokenEndpoint,
            new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode) throw await YouTubePublisher.SafeFailureAsync("YouTube OAuth exchange failed", response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var access = json.RootElement.TryGetProperty("access_token", out var accessNode) ? accessNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(access)) throw new PublishingApiException("YouTube OAuth returned no access token.");
        credentials["accessToken"] = access;
        if (json.RootElement.TryGetProperty("refresh_token", out var refreshNode) && !string.IsNullOrWhiteSpace(refreshNode.GetString()))
            credentials["refreshToken"] = refreshNode.GetString()!;
        var seconds = json.RootElement.TryGetProperty("expires_in", out var secondsNode) && secondsNode.TryGetInt32(out var parsed) ? parsed : 3600;
        var expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
        credentials["expiresAt"] = expires.ToString("O");
        var grantedScopes = json.RootElement.TryGetProperty("scope", out var scopeNode) ? scopeNode.GetString() : null;
        if (!string.IsNullOrWhiteSpace(grantedScopes) && !grantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(x => x is UploadScope or ManageScope))
            throw new PublishingApiException("Google did not grant the required YouTube upload scope.");
        credentials["grantedScopes"] = string.IsNullOrWhiteSpace(grantedScopes) ? UploadScope : grantedScopes;
        await connections.SaveCredentialSetAsync(PlatformKind.YouTubeShorts, credentials, expires, cancellationToken);
        _ = await connections.VerifyAsync(PlatformKind.YouTubeShorts, cancellationToken);
    }
}
