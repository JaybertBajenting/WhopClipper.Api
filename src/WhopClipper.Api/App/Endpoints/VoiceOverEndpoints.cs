using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

/// <summary>
/// The narrator voice for the narrated-luxury style: an ElevenLabs API key and voice id. The key is encrypted on this
/// machine and never sent back to the browser; writes need the same local session nonce as publishing.
/// </summary>
public static class VoiceOverEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var voice = api.MapGroup("/voice-over").WithTags("Voice-over");

        voice.MapGet("", async (VoiceOverSettingsStore store, CancellationToken ct) =>
            Results.Ok(VoiceOverSettingsStore.ToDto(await store.GetAsync(ct))));

        voice.MapPut("", async (UpdateVoiceOverRequest request, HttpContext context, LocalPublishingRequestGuard guard,
            VoiceOverSettingsStore store, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var key = request.ApiKey?.Trim();
            if (!string.IsNullOrEmpty(key) && (key.Length is < 20 or > 200 || key.Any(char.IsWhiteSpace)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["apiKey"] = ["That does not look like an ElevenLabs API key."] });
            var voiceId = request.VoiceId?.Trim();
            if (!string.IsNullOrEmpty(voiceId) && (voiceId.Length is < 10 or > 64 || !voiceId.All(char.IsAsciiLetterOrDigit)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["voiceId"] = ["A voice ID is the 20 letters and digits from the voice's ID button in ElevenLabs."] });
            var model = request.ModelId?.Trim();
            if (!string.IsNullOrEmpty(model) && (model.Length > 64 || !model.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["modelId"] = ["Use a model id such as eleven_multilingual_v2."] });
            return Results.Ok(VoiceOverSettingsStore.ToDto(await store.SaveAsync(key, voiceId, model, ct)));
        });

        // Free: reads the voice's name, which proves the key and the voice id both work.
        voice.MapPost("/test", async (HttpContext context, LocalPublishingRequestGuard guard, VoiceOverSettingsStore store,
            ElevenLabsClient client, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var settings = await store.GetAsync(ct);
            if (!settings.Configured || store.ApiKey(settings) is not { } key)
                return Results.Conflict(new { error = "Save an API key and a voice ID first." });
            try { return Results.Ok(new { voiceName = await client.VoiceNameAsync(key, settings.VoiceId, ct) }); }
            // A restricted key (text to speech only) is a good key: ElevenLabs recognised it and only refused to
            // list the voice. It once made this check report failure for a key that narrated fine.
            catch (InvalidOperationException ex) when (ex.Message.Contains("voices_read", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new { voiceName = "key accepted (restricted key, so the voice's name cannot be read; use Play sample to hear it)" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(title: "ElevenLabs check failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Spends a few credits: one short line read in the saved voice, to hear it before a batch.
        voice.MapPost("/sample", async (HttpContext context, LocalPublishingRequestGuard guard, VoiceOverSettingsStore store,
            ElevenLabsClient client, CancellationToken ct) =>
        {
            if (!guard.IsValid(context)) return Forbidden();
            var settings = await store.GetAsync(ct);
            if (!settings.Configured || store.ApiKey(settings) is not { } key)
                return Results.Conflict(new { error = "Save an API key and a voice ID first." });
            try
            {
                var (audio, _) = await client.SynthesizeAsync(key, settings.VoiceId, settings.ModelId,
                    "This millionaire trader made more before breakfast than most people make all year, and what he did next is crazy.", ct);
                return Results.File(audio, "audio/mpeg");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(title: "ElevenLabs sample failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private static IResult Forbidden() => Results.Problem(
        title: "Session expired",
        detail: "Refresh the local app and try the action again.",
        statusCode: StatusCodes.Status403Forbidden);
}
