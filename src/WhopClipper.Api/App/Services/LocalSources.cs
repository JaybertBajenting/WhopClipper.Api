using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed record LocalSourceScanResult(int Attached, int Added, int AlreadyKnown, IReadOnlyList<string> Notes);

/// <summary>
/// Registers video files the user placed in a campaign's source folder by hand. Downloading
/// is not always possible - a site may require a login the tooling cannot satisfy - so a
/// manually supplied file has to be a first-class way of getting footage into a campaign.
/// </summary>
public sealed partial class LocalSourceScanner(AppDbContext db, WorkspaceService workspace,
    ILogger<LocalSourceScanner> logger)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".ts", ".avi", ".flv" };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".vtt", ".ass" };

    public async Task<LocalSourceScanResult> ScanAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "source"));
        Directory.CreateDirectory(directory);

        var sources = await db.SourceAssets.Where(x => x.CampaignId == campaignId).ToListAsync(cancellationToken);
        var known = new HashSet<string>(sources.Where(x => !string.IsNullOrWhiteSpace(x.LocalRelativePath))
            .Select(x => x.LocalRelativePath!), StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            // A .part file is an interrupted download, not usable footage.
            .Where(x => VideoExtensions.Contains(Path.GetExtension(x)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attached = 0;
        var added = 0;
        var alreadyKnown = 0;
        var notes = new List<string>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceService.EnsureInside(directory, file);
            var relative = workspace.ToRelative(file);
            if (known.Contains(relative)) { alreadyKnown++; continue; }

            var fileName = Path.GetFileNameWithoutExtension(file);
            var match = MatchExistingSource(sources, fileName);
            // A hand-downloaded file rarely shares its base name with subtitles fetched
            // earlier, so fall back to the video id both names have in common.
            var subtitleFile = FindSubtitle(directory, fileName)
                ?? FindSubtitleByVideoId(directory, VideoIdFromUrl(match?.Url));
            var subtitle = subtitleFile is null ? null : workspace.ToRelative(subtitleFile);

            if (match is not null)
            {
                match.LocalRelativePath = relative;
                match.SubtitleRelativePath ??= subtitle;
                match.DownloadedAt ??= File.GetLastWriteTimeUtc(file);
                attached++;
                notes.Add($"{Path.GetFileName(file)} → {match.Title ?? match.Url}");
            }
            else
            {
                var source = new SourceAsset
                {
                    CampaignId = campaignId,
                    Url = string.Empty,
                    Title = fileName,
                    LocalRelativePath = relative,
                    SubtitleRelativePath = subtitle,
                    DownloadedAt = File.GetLastWriteTimeUtc(file),
                };
                db.SourceAssets.Add(source);
                sources.Add(source);
                added++;
                notes.Add($"{Path.GetFileName(file)} → added as a new source");
            }
            known.Add(relative);
        }

        if (attached + added > 0) await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Local source scan for {CampaignId}: {Attached} attached, {Added} added, {Known} already known",
            campaignId, attached, added, alreadyKnown);
        return new LocalSourceScanResult(attached, added, alreadyKnown, notes);
    }

    /// <summary>
    /// Registers reference clips dropped into the campaign's reference folder, mirroring the
    /// source scan. Returns the reference ids that need measuring.
    /// </summary>
    public async Task<(LocalSourceScanResult Result, IReadOnlyList<Guid> ToAnalyze)> ScanReferencesAsync(
        Guid campaignId, CancellationToken cancellationToken)
    {
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "reference"));
        Directory.CreateDirectory(directory);

        var references = await db.ReferenceVideos.Where(x => x.CampaignId == campaignId).ToListAsync(cancellationToken);
        var known = new HashSet<string>(references.Where(x => !string.IsNullOrWhiteSpace(x.LocalRelativePath))
            .Select(x => x.LocalRelativePath!), StringComparer.OrdinalIgnoreCase);

        var attached = 0;
        var added = 0;
        var alreadyKnown = 0;
        var notes = new List<string>();
        var toAnalyze = new List<Guid>();

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(x => VideoExtensions.Contains(Path.GetExtension(x)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceService.EnsureInside(directory, file);
            var relative = workspace.ToRelative(file);
            if (known.Contains(relative)) { alreadyKnown++; continue; }

            var fileName = Path.GetFileNameWithoutExtension(file);
            var match = references.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.LocalRelativePath)
                && VideoIdFromUrl(x.Url) is { Length: >= 6 } id
                && fileName.Contains(id, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                match.LocalRelativePath = relative;
                match.Status = ReferenceVideoStatus.Pending;
                attached++;
                toAnalyze.Add(match.Id);
                notes.Add($"{Path.GetFileName(file)} → {match.Label}");
            }
            else
            {
                var reference = new ReferenceVideo
                {
                    CampaignId = campaignId,
                    Label = fileName[..Math.Min(fileName.Length, 200)],
                    LocalRelativePath = relative,
                    IsPrimary = references.Count == 0,
                };
                db.ReferenceVideos.Add(reference);
                references.Add(reference);
                added++;
                toAnalyze.Add(reference.Id);
                notes.Add($"{Path.GetFileName(file)} → added as a new reference");
            }
            known.Add(relative);
        }

        if (attached + added > 0) await db.SaveChangesAsync(cancellationToken);
        return (new LocalSourceScanResult(attached, added, alreadyKnown, notes), toAnalyze);
    }

    /// <summary>
    /// Links a file to the source row it belongs to. yt-dlp keeps the site's video id in the
    /// file name, so that is the most reliable signal; the title is the fallback.
    /// </summary>
    private static SourceAsset? MatchExistingSource(IEnumerable<SourceAsset> sources, string fileName)
    {
        var candidates = sources.Where(x => string.IsNullOrWhiteSpace(x.LocalRelativePath)).ToList();
        if (candidates.Count == 0) return null;

        foreach (var candidate in candidates)
        {
            var id = VideoIdFromUrl(candidate.Url);
            if (id.Length >= 6 && fileName.Contains(id, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        var normalizedFile = Normalize(fileName);
        return candidates.FirstOrDefault(candidate =>
        {
            if (string.IsNullOrWhiteSpace(candidate.Title)) return false;
            var normalizedTitle = Normalize(candidate.Title);
            return normalizedTitle.Length >= 8
                && (normalizedFile.Contains(normalizedTitle, StringComparison.Ordinal)
                    || normalizedTitle.Contains(normalizedFile, StringComparison.Ordinal));
        });
    }

    internal static string VideoIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        var fromQuery = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith("v=", StringComparison.OrdinalIgnoreCase))?[2..];
        if (!string.IsNullOrWhiteSpace(fromQuery)) return fromQuery;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    /// <summary>Strips emoji, punctuation, and spacing so a renamed file still matches its title.</summary>
    internal static string Normalize(string value) =>
        NonAlphanumericRegex().Replace(value, string.Empty).ToLowerInvariant();

    private static string? FindSubtitle(string directory, string fileName)
    {
        foreach (var extension in SubtitleExtensions)
        {
            var exact = Path.Combine(directory, fileName + extension);
            if (File.Exists(exact)) return exact;
        }
        // yt-dlp writes language-tagged names such as "<title>.en.srt"; prefer English.
        return Directory.EnumerateFiles(directory, fileName + ".*", SearchOption.TopDirectoryOnly)
            .Where(x => SubtitleExtensions.Contains(Path.GetExtension(x)))
            .OrderBy(x => x.Contains(".en.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? FindSubtitleByVideoId(string directory, string videoId)
    {
        if (videoId.Length < 6) return null;
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(x => SubtitleExtensions.Contains(Path.GetExtension(x))
                && Path.GetFileNameWithoutExtension(x).Contains(videoId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Contains(".en.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}
