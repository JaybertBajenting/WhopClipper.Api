using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

public sealed record CropKeyframe(
    [property: JsonPropertyName("t")] double AtSeconds,
    [property: JsonPropertyName("x")] double X);

public sealed record SubjectTrack(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("detections")] int Detections,
    [property: JsonPropertyName("keyframes")] IReadOnlyList<CropKeyframe> Keyframes);

/// <summary>
/// Finds where the subject sits so SmartCrop can follow them. Cropping 16:9 to 9:16 keeps only
/// the middle ~31% of the width, and a streamer is rarely centred in their own webcam frame,
/// so a fixed centre crop cuts the face in half. Detection runs in a Python sidecar because
/// OpenCV lives there already, next to the transcriber.
/// </summary>
public sealed class SubjectTracker(IProcessRunner runner, IOptions<AppOptions> options,
    WorkspaceService workspace, ILogger<SubjectTracker> logger)
{
    /// <summary>
    /// Returns null when no subject could be found, or when Python or OpenCV is not usable.
    /// Callers fall back to the centre crop: a badly framed clip beats a failed render.
    /// </summary>
    public async Task<SubjectTrack?> TrackAsync(string mediaPath, double startSeconds, double endSeconds,
        int outputWidth, int outputHeight, string outputPath, CancellationToken cancellationToken)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "App", "Services", "framing.py");
        if (!File.Exists(script)) script = Path.Combine(AppContext.BaseDirectory, "framing.py");
        if (!File.Exists(script))
        {
            logger.LogWarning("framing.py was not found next to the application; SmartCrop will centre the frame.");
            return null;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var python = string.IsNullOrWhiteSpace(options.Value.PythonPath) ? "python" : options.Value.PythonPath!;
        var result = await runner.RunAsync(new ProcessInvocation(python,
            [script, mediaPath, outputPath,
                "--start", startSeconds.ToString("0.###", invariant),
                "--end", endSeconds.ToString("0.###", invariant),
                "--output-width", outputWidth.ToString(invariant),
                "--output-height", outputHeight.ToString(invariant)],
            workspace.Root, Timeout: TimeSpan.FromMinutes(10)), cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            logger.LogWarning("Subject tracking failed ({ExitCode}); SmartCrop will centre the frame. {Error}",
                result.ExitCode, Tail(result.StandardError));
            return null;
        }

        try
        {
            var track = JsonSerializer.Deserialize<SubjectTrack>(
                await File.ReadAllTextAsync(outputPath, cancellationToken), JsonDefaults.Serializer);
            if (track is null || track.Keyframes.Count == 0 || track.Width <= 0 || track.Height <= 0)
            {
                logger.LogInformation("No subject was found in the clip; SmartCrop will centre the frame.");
                return null;
            }
            logger.LogInformation("Subject tracked across {Detections} samples into {Keyframes} crop keyframes.",
                track.Detections, track.Keyframes.Count);
            return track;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Subject track was unreadable; SmartCrop will centre the frame.");
            return null;
        }
    }

    private static string Tail(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(2));
}
