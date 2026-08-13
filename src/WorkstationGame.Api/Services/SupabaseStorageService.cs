using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkstationGame.Api.Services;

public sealed class SupabaseStorageService(HttpClient httpClient, IConfiguration configuration)
{
    private string? BaseUrl => configuration["Game:SupabaseUrl"]?.TrimEnd('/');
    private string? ServiceKey => configuration["Game:SupabaseServiceRoleKey"];
    private string Bucket => configuration["Game:SupabaseStorageBucket"] ?? "workstations";

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
        return signedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? signedUrl
            : $"{BaseUrl}{signedUrl}";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceKey);
        request.Headers.TryAddWithoutValidation("apikey", ServiceKey);
        return request;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Supabase Storage is not configured on the backend.");
        }
    }
}

