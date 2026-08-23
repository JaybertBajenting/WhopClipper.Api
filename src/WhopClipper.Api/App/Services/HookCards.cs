using Microsoft.Extensions.Options;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Draws a hook overlay as a white rounded card with dark text, the way short-form clips title
/// themselves. FFmpeg's drawtext can only paint a square-cornered box, so the card is rendered
/// in a Python sidecar with Pillow - the same arrangement the transcriber and the subject
/// tracker already use - and overlaid as a transparent PNG.
/// </summary>
public sealed class HookCardRenderer(IProcessRunner runner, IOptions<AppOptions> options,
    ILogger<HookCardRenderer> logger)
{
    /// <summary>
    /// Returns null when the card could not be drawn, so the caller can fall back to plain
    /// outlined text: a clip with a less pretty title beats a clip that failed to render.
    /// </summary>
    public async Task<string?> RenderAsync(string text, string textFilePath, string outputPath,
        int frameWidth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var script = Path.Combine(AppContext.BaseDirectory, "App", "Services", "hookcard.py");
        if (!File.Exists(script)) script = Path.Combine(AppContext.BaseDirectory, "hookcard.py");
        if (!File.Exists(script))
        {
            logger.LogWarning("hookcard.py was not found next to the application; the hook will be drawn as plain text.");
            return null;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var python = string.IsNullOrWhiteSpace(options.Value.PythonPath) ? "python" : options.Value.PythonPath!;
        var result = await runner.RunAsync(new ProcessInvocation(python,
            [script, textFilePath, outputPath, "--frame-width", frameWidth.ToString(invariant)],
            Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory,
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            logger.LogWarning("Hook card rendering failed ({ExitCode}); the hook will be drawn as plain text. {Error}",
                result.ExitCode, Tail(result.StandardError));
            return null;
        }
        return outputPath;
    }

    private static string Tail(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty
            : text.Length <= 400 ? text.Trim() : text[^400..].Trim();
}
