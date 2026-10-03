using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

/// <param name="FromSeconds">Where to start in the linked video; leave empty for the beginning.</param>
public sealed record AddFootageRequest(string Url, double? FromSeconds = null, double? ToSeconds = null);

/// <summary>Replace the shots between <paramref name="StartSeconds"/> and <paramref name="EndSeconds"/> of the clip
/// with footage picked from the campaign's footage, in order.</summary>
public sealed record ManualBrollEditRequest(double StartSeconds, double EndSeconds, IReadOnlyList<ManualBrollShot> Shots);

/// <summary>A shot of the clip being edited that was cut from this footage.</summary>
/// <param name="InSeconds">Where in the footage the shot starts; null for shots placed before this was recorded.</param>
public sealed record FootageUseDto(int Shot, double? InSeconds, double DurationSeconds);

/// <summary>One video of the campaign's footage, as the editor of one clip sees it.</summary>
/// <param name="AddedHere">Pasted into this clip's editor, or already used in this clip.</param>
/// <param name="AddedIn">The title of the clip it was pasted into, when that is another clip.</param>
/// <param name="Progress">Where a download has got to; null once it has finished.</param>
public sealed record FootageDto(Guid Id, string Url, string Title, string? Channel, string? YouTubeId, double? FromSeconds,
    double? ToSeconds, double? SourceDurationSeconds, string Status, double DurationSeconds, int Width, int Height, string? Error,
    DateTimeOffset CreatedAt, bool AddedHere, string? AddedIn, IReadOnlyList<FootageUseDto> UsedHere, FootageProgress? Progress);

public static partial class PodcastSplitEndpoints
{
    /// <summary>
    /// The B-roll editor: the clip's lower-panel shots on the clip clock with the words spoken over each, the campaign's
    /// footage filled from pasted links, and a hand-picked replacement for any run of shots. A replacement goes through
    /// the same preview-then-apply path as an AI repair, so the soundtrack stays untouched and the old version is archived.
    /// </summary>
    private static void MapBrollEditor(RouteGroupBuilder api)
    {
        api.MapGet("/edit-plans/{planId:guid}/editor", async (Guid planId, AppDbContext db, WorkspaceService workspace,
            CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var split = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
            if (split is null) return Results.BadRequest(new { error = "Only podcast-split clips have a B-roll panel to edit." });
            var clock = 0d;
            var shots = new List<object>();
            for (var index = 0; index < split.Broll.Count; index++)
            {
                var shot = split.Broll[index];
                shots.Add(new
                {
                    index, startSeconds = Math.Round(clock, 3), durationSeconds = Math.Round(shot.DurationSeconds, 3),
                    shot.SourceTitle, shot.SourceUrl, shot.Query, shot.FootageId,
                    pickedByHand = shot.Query == JobExecutionService.ManualQuery,
                });
                clock += shot.DurationSeconds;
            }
            return Results.Ok(new
            {
                planId, title = split.Title, durationSeconds = Math.Round(clock, 3), shots, gaps = split.Gaps,
                words = split.Words, footage = await DescribeFootageAsync(plan, split, db, workspace, ct),
            });
        }).WithTags("B-roll editor");

        api.MapPost("/edit-plans/{planId:guid}/footage", async (Guid planId, AddFootageRequest request, AppDbContext db,
            WorkspaceService workspace, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var split = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
            if (split is null) return Results.BadRequest(new { error = "Only podcast-split clips have a footage bin." });
            var url = request.Url?.Trim() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https"))
                return Results.BadRequest(new { error = "Paste a full video link, starting with https://" });
            if (request.FromSeconds is < 0 || request.ToSeconds is <= 0 ||
                (request.FromSeconds is { } from && request.ToSeconds is { } to && to <= from))
                return Results.BadRequest(new { error = "The end time must come after the start time." });
            var folder = FootageBin.Folder(workspace, plan.CampaignId);
            // The same stretch of a video is never downloaded twice: footage that already holds it is handed back,
            // with where in it the stretch starts.
            if (FootageBin.FindCovering(FootageBin.List(folder), link.ToString(), request.FromSeconds, request.ToSeconds) is { } held)
                return Results.Ok(new { footage = held.Item, job = (object?)null, reused = true, inSeconds = held.InSeconds });
            var item = new FootageItem(Guid.NewGuid(), link.ToString(), link.ToString(), request.FromSeconds, request.ToSeconds,
                FootageBin.Downloading, null, 0, 0, 0, null, DateTimeOffset.UtcNow, AddedForPlanId: plan.Id);
            FootageBin.Write(folder, item);
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.FootageDownload,
                new FootageDownloadJobPayload(plan.Id, item.Id), ct);
            item = item with { JobId = job.Id };
            FootageBin.Write(folder, item);
            return Results.Accepted($"/api/jobs/{job.Id}",
                new { footage = item, job = job.ToDto(subjectName: split.Title), reused = false, inSeconds = 0d });
        }).WithTags("B-roll editor");

        api.MapGet("/edit-plans/{planId:guid}/footage/{footageId:guid}/file", async (Guid planId, Guid footageId,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var folder = FootageBin.Folder(workspace, plan.CampaignId);
            if (FootageBin.Find(folder, footageId) is not { } item || FootageBin.FilePath(folder, item) is not { } path)
                return Results.NotFound();
            var type = Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";
            return Results.File(path, type, enableRangeProcessing: true);
        }).WithTags("B-roll editor");

        api.MapDelete("/edit-plans/{planId:guid}/footage/{footageId:guid}", async (Guid planId, Guid footageId,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var folder = FootageBin.Folder(workspace, plan.CampaignId);
            if (FootageBin.Find(folder, footageId) is not { } item) return Results.NotFound();
            if (item.Status == FootageBin.Downloading && await DownloadIsActiveAsync(db, item, ct))
                return Results.Conflict(new { error = "Wait for the download to finish, or cancel it on the Jobs page." });
            // To the Recycle Bin: a video pulled from the footage by mistake can be restored. Clips it was already
            // placed in keep their own copy of the shot.
            FootageBin.Remove(folder, item, path =>
            {
                if (!(OperatingSystem.IsWindows() && RecycleBin.TrySend(path))) File.Delete(path);
            });
            return Results.NoContent();
        }).WithTags("B-roll editor");

        api.MapPost("/edit-plans/{planId:guid}/broll-edits", async (Guid planId, ManualBrollEditRequest request,
            AppDbContext db, WorkspaceService workspace, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var split = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
            if (split is null) return Results.BadRequest(new { error = "Only podcast-split clips have B-roll to edit." });
            BrollRepairSelection selection;
            try { selection = BrollRepairSupport.Select(split.Broll, request.StartSeconds, request.EndSeconds, BrollRepairSupport.EditorMaxSeconds); }
            catch (ArgumentOutOfRangeException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (request.Shots is not { Count: > 0 } picks || picks.Any(x => x.DurationSeconds <= 0 || x.InSeconds < 0) ||
                Math.Abs(picks.Sum(x => x.DurationSeconds) - selection.DurationSeconds) > 0.05)
                return Results.BadRequest(new { error = $"The picked footage must fill the selected shots exactly ({selection.DurationSeconds:0.0} s)." });
            var folder = FootageBin.Folder(workspace, plan.CampaignId);
            foreach (var pick in picks)
            {
                if (FootageBin.Find(folder, pick.FootageId) is not { Status: FootageBin.Ready } item || FootageBin.FilePath(folder, item) is null)
                    return Results.BadRequest(new { error = "Pick footage that has finished downloading." });
                if (pick.InSeconds + pick.DurationSeconds > item.DurationSeconds + 0.05)
                    return Results.BadRequest(new { error = $"\"{item.Title}\" is only {item.DurationSeconds:0.0} s long; start earlier in it." });
            }
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.BrollRepair,
                new BrollRepairJobPayload(plan.Id, selection.StartSeconds, selection.EndSeconds, "Picked in the B-roll editor",
                    AiMode.Claude, picks), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto(subjectName: split.Title));
        }).WithTags("B-roll editor");
    }

    /// <summary>A download whose job never ran or ended without saying so (the app closed, or it was cancelled while
    /// queued) is not active, and must not sit in the footage as "Downloading" forever.</summary>
    private static async Task<bool> DownloadIsActiveAsync(AppDbContext db, FootageItem item, CancellationToken ct) =>
        item.JobId is { } jobId && await db.Jobs.AsNoTracking()
            .AnyAsync(x => x.Id == jobId && (x.Status == JobStatus.Queued || x.Status == JobStatus.Running), ct);

    /// <summary>The campaign's footage, with what each video has to do with this clip and where downloads have got to.</summary>
    private static async Task<IReadOnlyList<FootageDto>> DescribeFootageAsync(EditPlan plan, PodcastSplitPlanDto split,
        AppDbContext db, WorkspaceService workspace, CancellationToken ct)
    {
        var folder = FootageBin.Folder(workspace, plan.CampaignId);
        var items = FootageBin.List(folder).ToList();

        var jobIds = items.Where(x => x.Status == FootageBin.Downloading).Select(x => x.JobId).OfType<Guid>().ToList();
        var jobs = jobIds.Count == 0 ? new Dictionary<Guid, JobStatus>()
            : await db.Jobs.AsNoTracking().Where(x => jobIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Status, ct);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.Status != FootageBin.Downloading) continue;
            var status = item.JobId is { } id && jobs.TryGetValue(id, out var known) ? known : (JobStatus?)null;
            if (status is JobStatus.Queued or JobStatus.Running) continue;
            items[index] = item with
            {
                Status = FootageBin.Failed,
                Error = status == JobStatus.Cancelled ? "The download was cancelled." : "The download stopped before it finished.",
            };
            FootageBin.Write(folder, items[index]);
        }

        var otherClips = items.Select(x => x.AddedForPlanId).OfType<Guid>().Where(x => x != plan.Id).Distinct().ToList();
        var titles = otherClips.Count == 0 ? new Dictionary<Guid, string?>()
            : (await db.EditPlans.AsNoTracking().Where(x => otherClips.Contains(x.Id))
                .Select(x => new { x.Id, x.ExtraSettingsJson, x.OutputFileName }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => JsonDefaults.Parse<EditPlanExtrasDto?>(x.ExtraSettingsJson, null)?.PodcastSplit?.Title
                is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(x.OutputFileName));

        return items.Select(item =>
        {
            var used = split.Broll.Select((shot, index) => (shot, index)).Where(x => x.shot.FootageId == item.Id)
                .Select(x => new FootageUseDto(x.index, x.shot.FootageInSeconds, Math.Round(x.shot.DurationSeconds, 3))).ToList();
            var progress = item.Status != FootageBin.Downloading ? null
                : item.JobId is { } id && jobs.TryGetValue(id, out var status) && status == JobStatus.Queued
                    ? new FootageProgress("Waiting for another download to finish")
                    : FootageBin.ProgressOf(item.Id) ?? new FootageProgress("Starting");
            var addedIn = item.AddedForPlanId is { } clip && clip != plan.Id ? titles.GetValueOrDefault(clip) : null;
            return new FootageDto(item.Id, item.Url, item.Title, item.Channel, FootageBin.YouTubeId(item.Url), item.FromSeconds,
                item.ToSeconds, item.SourceDurationSeconds, item.Status, item.DurationSeconds, item.Width, item.Height, item.Error,
                item.CreatedAt, item.AddedForPlanId is null || item.AddedForPlanId == plan.Id || used.Count > 0, addedIn, used, progress);
        }).ToList();
    }
}
