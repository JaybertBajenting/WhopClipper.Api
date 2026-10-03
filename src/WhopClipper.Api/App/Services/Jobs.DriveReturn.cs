using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>"Bring campaign back from Drive" for one campaign. It has no settings: the campaign is the job's own.</summary>
public sealed record DriveReturnJobPayload;

/// <summary>One video in Google Drive that "Bring campaign back" fetches: where it goes back to, and its size.</summary>
/// <param name="Group">"source" for a source video, "clip" for a finished clip.</param>
/// <param name="Bytes">What it takes on this PC, a clip's music-free preview included.</param>
public sealed record CampaignDriveReturnFile(string LocalPath, string Group, long Bytes, IReadOnlyList<Guid> SourceIds);

public static class CampaignDriveReturn
{
    /// <summary>
    /// Every video of the campaign that was moved to Google Drive and is not back yet, found by the move records left
    /// where the files were: finished clips under "output" and source videos under "source".
    /// </summary>
    public static IReadOnlyList<CampaignDriveReturnFile> Find(string campaignRoot)
    {
        var found = new List<CampaignDriveReturnFile>();
        var output = Path.Combine(campaignRoot, "output");
        if (Directory.Exists(output))
            foreach (var record in Records(output))
            {
                var clip = record[..^DriveArchive.RecordSuffix.Length] + ".mp4";
                if (!File.Exists(clip) && DriveArchive.Read(clip) is { } moved)
                    found.Add(new CampaignDriveReturnFile(clip, "clip", moved.Bytes, []));
            }
        var source = Path.Combine(campaignRoot, "source");
        if (Directory.Exists(source))
            foreach (var record in Records(source))
            {
                // A source's record names its one file, whatever kind of video it was.
                if (ReadRecord(record) is not { Files.Count: > 0 } moved) continue;
                var video = Path.Combine(Path.GetDirectoryName(record)!, moved.Files[0].Name);
                if (!File.Exists(video)) found.Add(new CampaignDriveReturnFile(video, "source", moved.Bytes, moved.SourceIds ?? []));
            }
        return found;
    }

    private static IEnumerable<string> Records(string folder) =>
        Directory.EnumerateFiles(folder, "*" + DriveArchive.RecordSuffix, SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    private static ArchivedClip? ReadRecord(string recordPath)
    {
        try { return JsonSerializer.Deserialize<ArchivedClip>(File.ReadAllText(recordPath), JsonDefaults.Serializer); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}

public sealed partial class JobExecutionService
{
    /// <summary>
    /// Brings a whole campaign back from Google Drive: every clip and source video it moved there. Each is downloaded
    /// and checked by size and MD5 before it counts as back; the copies in Drive stay. A clip shows on this PC again
    /// at once, and a source video's rows read "downloaded" again. One file failing does not stop the others.
    /// </summary>
    private async Task BringCampaignBackFromDriveAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var campaignId = job.CampaignId ?? throw new InvalidOperationException("Bringing back needs a campaign.");
        var root = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N")));
        var files = CampaignDriveReturn.Find(root);
        var total = files.Sum(x => x.Bytes);
        // Checked first, with the numbers: a disk that fills half way leaves a half-written video behind.
        var free = diskSpace.Available;
        if (free - total < options.Value.MinimumFreeDiskBytes)
            throw new InvalidOperationException(
                $"Bringing this campaign back needs {total / 1e9:0.0} GB, and this PC has {free / 1e9:0.0} GB free " +
                $"(keeping {options.Value.MinimumFreeDiskBytes / 1e9:0.0} GB spare). Nothing was downloaded.");
        var sources = await db.SourceAssets.Where(x => x.CampaignId == campaignId).ToListAsync(cancellationToken);

        var failures = new List<(string File, string Error)>();
        int clipsBack = 0, sourcesBack = 0, inARow = 0;
        long done = 0, fetched = 0;
        string Summary(bool finished) => JsonSerializer.Serialize(new
        {
            total = files.Count, clipsBack, sourcesBack, bytesFetched = fetched, failed = failures.Count, finished,
            failures = failures.Take(20).Select(x => new { file = x.File, error = x.Error }),
        }, JsonDefaults.Serializer);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file.LocalPath);
            try
            {
                await driveArchive.BringBackAsync(file.LocalPath, cancellationToken);
                fetched += file.Bytes;
                if (file.Group == "clip") clipsBack++;
                else
                {
                    sourcesBack++;
                    PointSourcesAt(sources, file);
                }
                inARow = 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failures.Add((name, ex.Message));
                logger.LogWarning("Bringing {File} back from Drive failed: {Error}", name, ex.Message);
                if (++inARow >= DriveUploadGiveUpAfter) break;
            }
            done += file.Bytes;
            job.Progress = Math.Clamp(.02 + .97 * done / Math.Max(1, total), .02, .99);
            job.ResultJson = Summary(false);
            await db.SaveChangesAsync(cancellationToken);
        }

        job.ResultJson = Summary(true);
        if (failures.Count == 0) return;
        throw new InvalidOperationException(
            $"{files.Count - clipsBack - sourcesBack} of {files.Count} videos did not come back, and they are still safe in " +
            $"Google Drive. First problem: {failures[0].Error} Press Bring back again to fetch what is left.");
    }

    /// <summary>
    /// Points the source rows that used a returned video at it again. The move recorded which rows those were; a move
    /// made before it did is matched by the video id in the file name, or by a local file's own name.
    /// </summary>
    private void PointSourcesAt(IReadOnlyList<SourceAsset> sources, CampaignDriveReturnFile file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.LocalPath);
        var rows = file.SourceIds.Count > 0
            ? sources.Where(x => file.SourceIds.Contains(x.Id))
            : sources.Where(x => x.LocalRelativePath is null &&
                                 (FootageBin.YouTubeId(x.Url) is { } id ? stem.Contains($"[{id}]", StringComparison.Ordinal)
                                     : string.Equals(x.Title, stem, StringComparison.OrdinalIgnoreCase)));
        foreach (var row in rows)
        {
            row.LocalRelativePath = workspace.ToRelative(file.LocalPath);
            row.DownloadedAt = DateTimeOffset.UtcNow;
        }
    }
}
