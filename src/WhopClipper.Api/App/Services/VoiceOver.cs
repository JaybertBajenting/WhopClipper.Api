using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>The saved narrator settings. The key is stored encrypted with this machine's data-protection keys.</summary>
public sealed record VoiceOverSettings(string? ProtectedApiKey, string? KeyHint, string VoiceId, string ModelId)
{
    /// <summary>Eleven v4, ElevenLabs' most expressive model as of October 2026, suits a hyped narrator.</summary>
    public const string DefaultModel = "eleven_v4";
    public static readonly VoiceOverSettings Empty = new(null, null, "", DefaultModel);
    public bool Configured => !string.IsNullOrWhiteSpace(ProtectedApiKey) && !string.IsNullOrWhiteSpace(VoiceId);
}

/// <summary>A finished narration: the audio file and when each word is spoken in it.</summary>
public sealed record Narration(string Path, double DurationSeconds, IReadOnlyList<TranscriptWord> Words, string Voice);

public sealed class VoiceOverSettingsStore(AppDbContext db, IDataProtectionProvider protection)
{
    private const string Key = "voice.elevenlabs";
    private readonly IDataProtector _protector = protection.CreateProtector("WhopClipper.ElevenLabsKey.v1");

    public async Task<VoiceOverSettings> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == Key, cancellationToken);
        return row is null ? VoiceOverSettings.Empty
            : JsonDefaults.Parse<VoiceOverSettings?>(row.Value, null) ?? VoiceOverSettings.Empty;
    }

    public async Task<VoiceOverSettings> SaveAsync(string? apiKey, string? voiceId, string? modelId, CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var key = apiKey?.Trim();
        var next = current with
        {
            ProtectedApiKey = string.IsNullOrEmpty(key) ? current.ProtectedApiKey : _protector.Protect(key),
            KeyHint = string.IsNullOrEmpty(key) ? current.KeyHint : Hint(key),
            VoiceId = voiceId?.Trim() is { Length: > 0 } voice ? voice : current.VoiceId,
            ModelId = modelId?.Trim() is { Length: > 0 } model ? model : current.ModelId,
        };
        var row = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == Key, cancellationToken);
        if (row is null) db.ApplicationSettings.Add(row = new ApplicationSetting { Key = Key });
        row.Value = JsonSerializer.Serialize(next, JsonDefaults.Serializer);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return next;
    }

    public string? ApiKey(VoiceOverSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ProtectedApiKey)) return null;
        try { return _protector.Unprotect(settings.ProtectedApiKey); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public static VoiceOverSettingsDto ToDto(VoiceOverSettings settings) =>
        new(settings.Configured, settings.KeyHint, settings.VoiceId, settings.ModelId);

    /// <summary>Enough of the key to recognise which one is saved, never enough to use it.</summary>
    private static string Hint(string key) => key.Length <= 8 ? "••••" : $"{key[..3]}…{key[^4..]}";
}

/// <summary>
/// ElevenLabs text to speech. The "with-timestamps" call returns the audio and when each character is spoken, which
/// gives the one-word captions their timing without transcribing the narration again.
/// </summary>
public sealed class ElevenLabsClient(IHttpClientFactory clients)
{
    public const string BaseUrl = "https://api.elevenlabs.io";

    /// <summary>
    /// The narration's audio, and when each word is spoken. The timings come from the "with-timestamps" call; a model
    /// that refuses that call (the documentation does not promise it for v3 or v4) is asked for plain audio instead, and
    /// the words come back empty so the caller times them itself.
    /// </summary>
    public async Task<(byte[] Audio, IReadOnlyList<TranscriptWord> Words)> SynthesizeAsync(string apiKey, string voiceId,
        string modelId, string text, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        var body = JsonSerializer.Serialize(new { text, model_id = modelId, voice_settings = VoiceSettings(modelId) });
        var voice = Uri.EscapeDataString(voiceId);

        using (var timed = await PostAsync(client, $"{BaseUrl}/v1/text-to-speech/{voice}/with-timestamps?output_format=mp3_44100_128",
                   apiKey, body, "application/json", cancellationToken))
        {
            var answer = await timed.Content.ReadAsStringAsync(cancellationToken);
            if (timed.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(answer);
                var root = document.RootElement;
                var audio = Convert.FromBase64String(root.GetProperty("audio_base64").GetString() ?? "");
                var alignment = root.TryGetProperty("alignment", out var exact) && exact.ValueKind == JsonValueKind.Object ? exact
                    : root.TryGetProperty("normalized_alignment", out var normal) && normal.ValueKind == JsonValueKind.Object ? normal
                    : (JsonElement?)null;
                return (audio, alignment is { } found ? WordsFromAlignment(found) : []);
            }
            // A bad key, no credits or a rate limit would fail the plain call the same way: report it now.
            if ((int)timed.StatusCode is not (400 or 404 or 405 or 422))
                throw new InvalidOperationException($"ElevenLabs refused the narration ({(int)timed.StatusCode}): {ErrorOf(answer)}");
        }

        using var plain = await PostAsync(client, $"{BaseUrl}/v1/text-to-speech/{voice}?output_format=mp3_44100_128",
            apiKey, body, "audio/mpeg", cancellationToken);
        if (!plain.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"ElevenLabs refused the narration ({(int)plain.StatusCode}): {ErrorOf(await plain.Content.ReadAsStringAsync(cancellationToken))}");
        return (await plain.Content.ReadAsByteArrayAsync(cancellationToken), []);
    }

    /// <summary>
    /// Voice settings each model accepts. Eleven v3 and v4 take only stability and similarity (v4 has no style or
    /// speed, and v3's stability is one of three presets), so the older models' style and speaker boost are not sent.
    /// </summary>
    public static object VoiceSettings(string modelId) =>
        modelId.StartsWith("eleven_v3", StringComparison.OrdinalIgnoreCase) || modelId.StartsWith("eleven_v4", StringComparison.OrdinalIgnoreCase)
            ? new { stability = 0.5, similarity_boost = 0.8 }
            : new { stability = 0.45, similarity_boost = 0.8, style = 0.25, use_speaker_boost = true };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string apiKey, string body,
        string accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>The voice's display name, which also proves the key and the voice id both work.</summary>
    public async Task<string> VoiceNameAsync(string apiKey, string voiceId, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/voices/{Uri.EscapeDataString(voiceId)}");
        request.Headers.Add("xi-api-key", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs did not accept the key and voice ({(int)response.StatusCode}): {ErrorOf(body)}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? voiceId : voiceId;
    }

    /// <summary>Groups the per-character timings into words: each run of non-space characters, from its first
    /// character's start to its last character's end.</summary>
    public static IReadOnlyList<TranscriptWord> WordsFromAlignment(JsonElement alignment)
    {
        var characters = alignment.GetProperty("characters").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        var starts = alignment.GetProperty("character_start_times_seconds").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var ends = alignment.GetProperty("character_end_times_seconds").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var count = Math.Min(characters.Count, Math.Min(starts.Count, ends.Count));
        var words = new List<TranscriptWord>();
        var text = new StringBuilder();
        double start = 0, end = 0;
        for (var index = 0; index <= count; index++)
        {
            var character = index < count ? characters[index] : " ";
            if (string.IsNullOrWhiteSpace(character))
            {
                // An audio tag ("[excited]") steers the voice and is not spoken, so it never becomes a caption.
                if (text.Length > 0 && !(text[0] == '[' && text[^1] == ']'))
                    words.Add(new TranscriptWord(Math.Round(start, 3), Math.Round(end, 3), text.ToString()));
                text.Clear();
                continue;
            }
            if (text.Length == 0) start = starts[index];
            text.Append(character);
            end = ends[index];
        }
        return words;
    }

    private static string ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var detail = document.RootElement.TryGetProperty("detail", out var value) ? value : document.RootElement;
            if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("message", out var message))
                return message.GetString() ?? body;
            return detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? body : detail.GetRawText();
        }
        catch (JsonException) { return body.Length <= 300 ? body : body[..300]; }
    }
}
