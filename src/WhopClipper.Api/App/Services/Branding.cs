using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Stores the campaign's client logo and measures where its artwork actually sits inside the
/// file. Brand kits hand over the mark on a full-frame transparent sheet, so burning the file
/// in as supplied would stretch a 16:9 sheet across a 9:16 clip. Measuring the opaque box once
/// on upload lets the renderer place the mark itself, at whatever size the platform wants.
/// </summary>
public sealed partial class CampaignBrandingService(WorkspaceService workspace, IProcessRunner runner,
    IOptions<AppOptions> options, ILogger<CampaignBrandingService> logger)
{
    public const long MaxUploadBytes = 8L * 1024 * 1024;

    private static readonly string[] SupportedExtensions = [".png", ".webp"];

    public static string AcceptedExtensions => string.Join(", ", SupportedExtensions);

    public static bool IsSupported(string fileName) =>
        SupportedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public async Task<CampaignBrandingDto> StoreAsync(Guid campaignId, string originalFileName, Stream content,
        CampaignBrandingDto? existing, CancellationToken cancellationToken)
    {
        if (!IsSupported(originalFileName))
            throw new InvalidOperationException($"Unsupported logo format. Accepted formats: {AcceptedExtensions}.");
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "campaign", "branding"));
        Directory.CreateDirectory(directory);
        var safeName = WorkspaceService.SafeFileName(Path.GetFileName(originalFileName), "logo");
        var fullPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{safeName}");
        WorkspaceService.EnsureInside(directory, fullPath);

        await using (var file = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        var crop = await MeasureArtworkAsync(fullPath, cancellationToken);
        var baseline = existing ?? new CampaignBrandingDto(null);
        return baseline with
        {
            LogoRelativePath = workspace.ToRelative(fullPath),
            LogoFileName = safeName,
            LogoCropRect = crop,
            Enabled = true
        };
    }

    /// <summary>
    /// Returns an FFmpeg "w:h:x:y" box around the non-transparent pixels, or null when the image
    /// carries no alpha to trim and the whole frame is the artwork. alphaextract turns the alpha
    /// channel into a greyscale frame, which is the one thing cropdetect knows how to bound.
    /// </summary>
    public async Task<string?> MeasureArtworkAsync(string fullPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessInvocation(options.Value.FfmpegPath,
            // skip=0 matters: cropdetect ignores the first two frames by default, and a logo is
            // one frame, so the default settles on measuring nothing at all.
            ["-hide_banner", "-nostats", "-i", fullPath,
                "-vf", "alphaextract,cropdetect=limit=0:round=2:reset=0:skip=0", "-frames:v", "1", "-f", "null", "-"],
            workspace.Root, Timeout: TimeSpan.FromSeconds(30)), cancellationToken);
        if (result.ExitCode != 0)
        {
            logger.LogInformation("No alpha channel to measure on {FileName}; the whole image will be burned in.",
                Path.GetFileName(fullPath));
            return null;
        }
        var match = CropDetectPattern().Matches(result.StandardError).LastOrDefault();
        return match?.Groups[1].Value;
    }

    public void DeleteFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        try
        {
            var fullPath = workspace.ResolveRelative(relativePath);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (InvalidOperationException ex) { logger.LogWarning(ex, "Refused to delete a logo outside the workspace."); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not delete a replaced campaign logo."); }
    }

    [GeneratedRegex(@"crop=(\d{1,5}:\d{1,5}:\d{1,5}:\d{1,5})")]
    private static partial Regex CropDetectPattern();
}
