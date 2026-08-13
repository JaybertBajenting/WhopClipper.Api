using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkstationGame.Api.Services;

public sealed class SupabaseStorageService(HttpClient httpClient, IConfiguration configuration)
{
    private string? BaseUrl => NormalizeProjectUrl(configuration["Game:SupabaseUrl"]);
    private string? ServiceKey => configuration["Game:SupabaseServiceRoleKey"]?.Trim();
    private string Bucket => configuration["Game:SupabaseStorageBucket"]?.Trim() ?? "workstations";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ServiceKey);

    public async Task<string> UploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            throw new InvalidOperationException("Only JPG, PNG, and WebP images are supported.");
        }

        var path = $"games/{DateTimeOffset.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{extension}";
        using var request = CreateRequest(HttpMethod.Post, $"/storage/v1/object/{Bucket}/{path}");
        await using var stream = file.OpenReadStream();
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        request.Headers.TryAddWithoutValidation("x-upsert", "false");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Supabase Storage upload failed: {body}");
        }

        return path;
    }

    public async Task<string> CreateSignedUrlAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Post, $"/storage/v1/object/sign/{Bucket}/{path}");
        request.Content = new StringContent("{\"expiresIn\":3600}", Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not sign workstation image: {body}");
        }

        using var json = JsonDocument.Parse(body);
        var signedUrl = json.RootElement.GetProperty("signedURL").GetString()
                        ?? throw new InvalidOperationException("Supabase returned an empty signed URL.");
        if (signedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return signedUrl;
        }

        var relativePath = signedUrl.StartsWith('/') ? signedUrl : $"/{signedUrl}";
        return relativePath.StartsWith("/storage/v1/", StringComparison.OrdinalIgnoreCase)
            ? $"{BaseUrl}{relativePath}"
            : $"{BaseUrl}/storage/v1{relativePath}";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.TryAddWithoutValidation("apikey", ServiceKey);

        // Supabase's current sb_secret_* keys are opaque API keys, not JWTs, and
        // must not be sent as Bearer tokens. Legacy service_role JWTs still need
        // Authorization in addition to the apikey header.
        if (!ServiceKey!.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase) &&
            !ServiceKey.StartsWith("sb_publishable_", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceKey);
        }

        return request;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Supabase Storage is not configured on the backend.");
        }
    }

    private static string? NormalizeProjectUrl(string? value)
    {
        var url = value?.Trim().TrimEnd('/');
        const string dataApiSuffix = "/rest/v1";
        return url?.EndsWith(dataApiSuffix, StringComparison.OrdinalIgnoreCase) == true
            ? url[..^dataApiSuffix.Length]
            : url;
    }
}
