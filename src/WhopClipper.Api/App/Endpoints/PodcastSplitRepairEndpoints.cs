using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

public sealed record StartBrollRepairRequest(double StartSeconds, double EndSeconds, string? Note = null,
    AiMode Mode = AiMode.Compare);

public static partial class PodcastSplitEndpoints
{
    private static void MapBrollRepair(RouteGroupBuilder api)
    {
        api.MapPost("/edit-plans/{planId:guid}/broll-repairs", async (Guid planId, StartBrollRepairRequest request,
            AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var split = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
            if (split is null) return Results.BadRequest(new { error = "Only podcast-split clips have repairable B-roll." });
            if (request.Note?.Length > 400) return Results.BadRequest(new { error = "The repair note must be at most 400 characters." });
            try { BrollRepairSupport.Select(split.Broll, request.StartSeconds, request.EndSeconds); }
            catch (ArgumentOutOfRangeException ex) { return Results.BadRequest(new { error = ex.Message }); }
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.BrollRepair,
                new BrollRepairJobPayload(plan.Id, request.StartSeconds, request.EndSeconds, request.Note, request.Mode), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto(subjectName: split.Title));
        }).WithTags("B-roll repair");

        api.MapGet("/edit-plans/{planId:guid}/broll-repairs/{repairId:guid}", async (Guid planId, Guid repairId,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var loaded = await LoadCandidateAsync(planId, repairId, db, workspace, ct);
            if (loaded is null) return Results.NotFound();
            var (candidate, _) = loaded.Value;
            return Results.Ok(new
            {
                candidate.RepairId, candidate.PlanId, candidate.StartSeconds, candidate.EndSeconds,
                candidate.SpokenText, candidate.CreatedAt,
                shots = candidate.ReplacementShots.Select(x => new { x.Query, x.SourceTitle, x.SourceUrl, x.DurationSeconds }),
                previewUrl = $"/api/edit-plans/{planId}/broll-repairs/{repairId}/file",
            });
        }).WithTags("B-roll repair");

        api.MapGet("/edit-plans/{planId:guid}/broll-repairs/{repairId:guid}/file", async (Guid planId, Guid repairId,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var loaded = await LoadCandidateAsync(planId, repairId, db, workspace, ct);
            if (loaded is null) return Results.NotFound();
            var (candidate, _) = loaded.Value;
            var path = workspace.ResolveRelative(candidate.CandidateRelativePath);
            return File.Exists(path) ? Results.File(path, "video/mp4", enableRangeProcessing: true) : Results.NotFound();
        }).WithTags("B-roll repair");

        api.MapPost("/edit-plans/{planId:guid}/broll-repairs/{repairId:guid}/apply", async (Guid planId, Guid repairId,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var loaded = await LoadCandidateAsync(planId, repairId, db, workspace, ct);
            if (loaded is null) return Results.NotFound();
            var (candidate, repairRoot) = loaded.Value;
            var plan = await db.EditPlans.AsNoTracking().SingleAsync(x => x.Id == planId, ct);
            var originalJson = plan.ExtraSettingsJson ?? "";
            if (BrollRepairSupport.Sha256(originalJson) != candidate.OriginalPlanHash)
                return Results.Conflict(new { error = "This clip changed after the preview was made. Start a new repair so the footage matches its current edit." });
            var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(originalJson, null);
            var split = extras?.PodcastSplit;
            if (split is null) return Results.Conflict(new { error = "This is no longer a podcast-split plan." });
            BrollRepairSelection selection;
            // Only re-checks that the shots still line up: an editor edit may span more than an AI repair's 20 s.
            try { selection = BrollRepairSupport.Select(split.Broll, candidate.StartSeconds, candidate.EndSeconds, BrollRepairSupport.EditorMaxSeconds); }
            catch (ArgumentOutOfRangeException) { return Results.Conflict(new { error = "The saved B-roll timing changed." }); }
            if (Math.Abs(selection.StartSeconds - candidate.StartSeconds) > .02 ||
                Math.Abs(selection.EndSeconds - candidate.EndSeconds) > .02)
                return Results.Conflict(new { error = "The saved B-roll timing changed." });
            var updated = BrollRepairSupport.Replace(split.Broll, selection, candidate.ReplacementShots);
            var outputDirectory = workspace.ResolveRelative(Path.Combine("campaigns", plan.CampaignId.ToString("N"),
                "output", plan.Platform.ToString().ToLowerInvariant()));
            var stem = WorkspaceService.SafeFileName(Path.GetFileNameWithoutExtension(plan.OutputFileName), "clip");
            var output = Path.Combine(outputDirectory, $"{stem}-{plan.Id:N}.mp4");
            var voice = Path.Combine(outputDirectory, $"{stem}-{plan.Id:N}.preview.mp4");
            var staged = workspace.ResolveRelative(candidate.CandidateRelativePath);
            var stagedVoice = candidate.CandidatePreviewRelativePath is null ? null : workspace.ResolveRelative(candidate.CandidatePreviewRelativePath);
            if (!File.Exists(output) || !File.Exists(staged) ||
                (stagedVoice is not null && (!File.Exists(voice) || !File.Exists(stagedVoice))))
                return Results.Conflict(new { error = "The current clip or reviewed candidate is missing." });
            if (BrollRepairSupport.Sha256File(output) != candidate.OriginalVideoHash)
                return Results.Conflict(new { error = "The video changed after the preview was made. Start a new repair." });
            foreach (var shot in candidate.ReplacementShots)
            {
                var path = workspace.ResolveRelative(shot.RelativePath);
                WorkspaceService.EnsureInside(repairRoot, path);
                if (!File.Exists(path)) return Results.Conflict(new { error = "A reviewed footage source is missing." });
            }
            var history = workspace.ResolveRelative(Path.Combine("campaigns", plan.CampaignId.ToString("N"),
                "intermediate", "broll-repair-history", plan.Id.ToString("N"), repairId.ToString("N")));
            if (Directory.Exists(history)) return Results.Conflict(new { error = "This repair has already been applied." });
            Directory.CreateDirectory(history);
            var oldVideo = Path.Combine(history, Path.GetFileName(output));
            var oldVoice = Path.Combine(history, Path.GetFileName(voice));
            File.Copy(output, oldVideo);
            if (File.Exists(voice)) File.Copy(voice, oldVoice);
            await File.WriteAllTextAsync(Path.Combine(history, "plan-before.json"), JsonSerializer.Serialize(new
            {
                plan.Id, plan.ExtraSettingsJson, plan.LastFfmpegArgumentsJson, plan.SubtitleRelativePath,
                candidate.StartSeconds, candidate.EndSeconds, candidate.RepairId
            }, JsonDefaults.Serializer), ct);
            var newJson = JsonSerializer.Serialize(extras! with { PodcastSplit = split with { Broll = updated } }, JsonDefaults.Serializer);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                File.Move(staged, output, true);
                if (stagedVoice is not null) File.Move(stagedVoice, voice, true);
                var changed = await db.EditPlans.Where(x => x.Id == planId && x.ExtraSettingsJson == originalJson)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExtraSettingsJson, newJson), ct);
                if (changed != 1) throw new InvalidOperationException("The edit plan changed while the repair was being applied.");
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                if (File.Exists(output) && !File.Exists(staged)) File.Copy(output, staged);
                if (stagedVoice is not null && File.Exists(voice) && !File.Exists(stagedVoice)) File.Copy(voice, stagedVoice);
                File.Copy(oldVideo, output, true);
                if (File.Exists(oldVoice)) File.Copy(oldVoice, voice, true);
                throw;
            }
            return Results.Ok(new { applied = true, archivedRelativePath = workspace.ToRelative(history),
                outputRelativePath = workspace.ToRelative(output), changedSeconds = new[] { candidate.StartSeconds, candidate.EndSeconds } });
        }).WithTags("B-roll repair");
    }

    private static async Task<(BrollRepairCandidate Candidate, string Root)?> LoadCandidateAsync(Guid planId, Guid repairId,
        AppDbContext db, WorkspaceService workspace, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == repairId &&
            x.Kind == JobKind.BrollRepair && x.Status == JobStatus.Completed, ct);
        if (job is null) return null;
        var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId && x.CampaignId == job.CampaignId, ct);
        if (plan is null) return null;
        var root = workspace.ResolveRelative(Path.Combine("campaigns", plan.CampaignId.ToString("N"),
            "intermediate", "broll-repairs", repairId.ToString("N")));
        var manifest = Path.Combine(root, "candidate.json");
        if (!File.Exists(manifest)) return null;
        var candidate = JsonSerializer.Deserialize<BrollRepairCandidate>(await File.ReadAllTextAsync(manifest, ct), JsonDefaults.Serializer);
        if (candidate?.PlanId != planId || candidate.RepairId != repairId) return null;
        WorkspaceService.EnsureInside(root, workspace.ResolveRelative(candidate.CandidateRelativePath));
        return (candidate, root);
    }
}
