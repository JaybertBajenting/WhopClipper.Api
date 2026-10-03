using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>"Move campaign to Drive" for one campaign. It has no settings: the campaign is the job's own.</summary>
public sealed record DriveUploadJobPayload;

/// <summary>One file "Move campaign to Drive" sends: where it is, which Drive folder takes it, and what it is.</summary>
/// <param name="Group">"source" for a downloaded source video, "clip" for a finished clip.</param>
/// <param name="Bytes">What leaving frees on this PC: for a clip, its music-free preview too.</param>
public sealed record CampaignDriveFile(string LocalPath, string RemoteFolder, string Group, long Bytes);

/// <summary>What a campaign holds for "Move campaign to Drive", read from its folders.</summary>
/// <param name="AlreadyMoved">Clips whose video was moved to Drive earlier and is no longer on this PC.</param>
/// <param name="StillWriting">Files changed in the last minute and a half: a render or download in progress.</param>
public sealed record CampaignDriveFiles(IReadOnlyList<CampaignDriveFile> Files, int AlreadyMoved, int StillWriting)
{
    /// <summary>A file changed this recently may still be growing; sending it would move half a video.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(90);

    public const string SourcesFolder = "sources";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".ts" };

    /// <summary>
    /// The campaign's source videos (its "source" folder) and finished clips (its "output" folder). Clips go where
    /// "Move to Google Drive" on a single clip puts them; sources go into a "sources" folder beside them. Left out:
    /// half-downloaded files, working files under "intermediate", and clips set aside in "skipped". A clip's
    /// music-free preview is not listed on its own: it travels with its clip.
    /// </summary>
    public static CampaignDriveFiles Find(string campaignRoot, string remote, DateTime nowUtc)
    {
        var files = new List<CampaignDriveFile>();
        var writing = 0;
        void Add(string folder, string remoteFolder, string group, Func<string, bool> wanted, Func<string, long> extraBytes)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                         .Where(x => VideoExtensions.Contains(Path.GetExtension(x)) && wanted(x))
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                if (info.Length == 0) continue;
                if (nowUtc - info.LastWriteTimeUtc < Settle) { writing++; continue; }
                files.Add(new CampaignDriveFile(path, remoteFolder, group, info.Length + extraBytes(path)));
            }
        }
        Add(Path.Combine(campaignRoot, "source"), $"{remote}/{SourcesFolder}", "source", _ => true, _ => 0);
        var output = Path.Combine(campaignRoot, "output");
        Add(output, remote, "clip", x => !x.EndsWith(".preview.mp4", StringComparison.OrdinalIgnoreCase),
            x => new FileInfo(DriveArchive.PreviewPath(x)) is { Exists: true } preview ? preview.Length : 0);
        var moved = Directory.Exists(output)
            ? Directory.EnumerateFiles(output, "*" + DriveArchive.RecordSuffix, SearchOption.AllDirectories)
                .Count(x => !File.Exists(x[..^DriveArchive.RecordSuffix.Length] + ".mp4"))
            : 0;
        return new CampaignDriveFiles(files, moved, writing);
    }

    /// <summary>
    /// Whether a clip file belongs to one of these edit plans. Renders are named "&lt;title&gt;-&lt;plan id&gt;" or,
    /// for the older styles, "&lt;plan's output name&gt;-&lt;job id&gt;".
    /// </summary>
    public static bool BelongsTo(string clipPath, IEnumerable<(Guid Id, string OutputFileName)> plans)
    {
        var stem = Path.GetFileNameWithoutExtension(clipPath);
        return plans.Any(plan => stem.EndsWith("-" + plan.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
            (Path.GetFileNameWithoutExtension(plan.OutputFileName) is { Length: > 0 } name &&
             stem.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed partial class JobExecutionService
{
    /// <summary>How many files in a row may fail before the rest are left: three failures running means the cause is
    /// the connection or the Google sign-in, and it would fail every file after them too.</summary>
    private const int DriveUploadGiveUpAfter = 3;

    /// <summary>Publish states in which a YouTube upload still needs the clip's file on this PC.</summary>
    private static readonly PublishStatus[] StillNeedsItsFile =
        [PublishStatus.Queued, PublishStatus.Validating, PublishStatus.Uploading, PublishStatus.Processing,
         PublishStatus.Scheduled, PublishStatus.NeedsAttention];

    /// <summary>
    /// Moves a whole campaign to Google Drive: every source video and every finished clip, one after another. Each is
    /// uploaded, checked there by size and MD5, and only then removed from this PC. Clips keep their place in the
    /// gallery by thumbnail, with Bring back; a source's row goes back to "not downloaded". A clip waiting to go up on
    /// YouTube, or being re-rendered, stays. One file failing does not stop the others.
    /// </summary>
    private async Task UploadCampaignToDriveAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var campaignId = job.CampaignId ?? throw new InvalidOperationException("The move has no campaign.");
        var campaign = await db.Campaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId, cancellationToken);
        var root = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N")));
        var remote = driveArchive.RemoteFor(campaign.Name);
        var found = CampaignDriveFiles.Find(root, remote, DateTime.UtcNow);
        var total = Math.Max(1L, found.Files.Sum(x => x.Bytes));

        var scheduled = (await db.PublishJobs.AsNoTracking()
                .Where(x => x.CampaignId == campaignId && StillNeedsItsFile.Contains(x.Status))
                .Select(x => x.OutputRelativePath).ToListAsync(cancellationToken))
            .Select(x => x.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = await db.SourceAssets.Where(x => x.CampaignId == campaignId && x.LocalRelativePath != null)
            .ToListAsync(cancellationToken);

        IReadOnlyDictionary<string, ArchivedFile>? sourceListing = null;
        var failures = new List<(string File, string Error)>();
        int clipsMoved = 0, sourcesMoved = 0, keptForYouTube = 0, keptRendering = 0, inARow = 0;
        long done = 0, freed = 0;
        string Summary(bool finished) => JsonSerializer.Serialize(new
        {
            remote, total = found.Files.Count, clipsMoved, sourcesMoved, bytesFreed = freed, keptForYouTube, keptRendering,
            stillWriting = found.StillWriting, alreadyMoved = found.AlreadyMoved, failed = failures.Count, finished,
            failures = failures.Take(20).Select(x => new { file = x.File, error = x.Error }),
        }, JsonDefaults.Serializer);

        foreach (var file in found.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file.LocalPath);
            try
            {
                if (file.Group == "clip")
                {
                    if (scheduled.Contains(workspace.ToRelative(file.LocalPath).Replace('\\', '/'))) keptForYouTube++;
                    else if (await IsBeingRenderedAsync(campaignId, file.LocalPath, cancellationToken)) keptRendering++;
                    else
                    {
                        freed += (await driveArchive.MoveOutAsync(campaign.Name, file.LocalPath, cancellationToken)).Bytes;
                        clipsMoved++;
                    }
                }
                else
                {
                    sourceListing ??= await driveArchive.ListAsync(file.RemoteFolder, cancellationToken);
                    var rows = sources.Where(x => x.LocalRelativePath is not null && string.Equals(
                            Path.GetFullPath(workspace.ResolveRelative(x.LocalRelativePath)), Path.GetFullPath(file.LocalPath),
                            StringComparison.OrdinalIgnoreCase)).ToList();
                    freed += (await driveArchive.MoveFileAsync(file.RemoteFolder, file.LocalPath,
                        sourceListing.GetValueOrDefault(name), rows.Select(x => x.Id).ToList(), cancellationToken)).Bytes;
                    sourcesMoved++;
                    // The row goes back to "not downloaded", as it does when a clip run removes its source.
                    foreach (var source in rows)
                    {
                        source.LocalRelativePath = null;
                        source.DownloadedAt = null;
                    }
                }
                inARow = 0;
            }
            // rclone's failures, and a file another program holds open or that vanished since the folders were read.
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failures.Add((name, ex.Message));
                logger.LogWarning("Moving {File} to Drive failed: {Error}", name, ex.Message);
                if (++inARow >= DriveUploadGiveUpAfter) break;
            }
            done += file.Bytes;
            job.Progress = Math.Clamp(.02 + .97 * done / total, .02, .99);
            job.ResultJson = Summary(false);
            await db.SaveChangesAsync(cancellationToken);
        }

        job.ResultJson = Summary(true);
        if (failures.Count == 0) return;
        var left = found.Files.Count - clipsMoved - sourcesMoved - keptForYouTube - keptRendering;
        throw new InvalidOperationException(
            $"{left} of {found.Files.Count} videos did not reach Google Drive, so they are still on this PC. " +
            $"First problem: {failures[0].Error} Press Move campaign to Drive again to send what is left.");
    }

    /// <summary>True while a render of this clip is queued or running: it is about to rewrite the file.</summary>
    private async Task<bool> IsBeingRenderedAsync(Guid campaignId, string clipPath, CancellationToken cancellationToken)
    {
        var payloads = await db.Jobs.AsNoTracking()
            .Where(x => x.CampaignId == campaignId && x.Kind == JobKind.Render &&
                        (x.Status == JobStatus.Queued || x.Status == JobStatus.Running))
            .Select(x => x.PayloadJson).ToListAsync(cancellationToken);
        var planIds = payloads.Select(x => JsonDefaults.Parse<RenderJobPayload?>(x, null)?.EditPlanId)
            .OfType<Guid>().Distinct().ToList();
        if (planIds.Count == 0) return false;
        var plans = await db.EditPlans.AsNoTracking().Where(x => planIds.Contains(x.Id))
            .Select(x => new { x.Id, x.OutputFileName }).ToListAsync(cancellationToken);
        return CampaignDriveFiles.BelongsTo(clipPath, plans.Select(x => (x.Id, x.OutputFileName)));
    }
}
