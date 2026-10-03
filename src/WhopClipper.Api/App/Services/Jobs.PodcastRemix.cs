using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed partial class JobExecutionService
{
    private async Task RemixPodcastAsync(JobRecord job, EditPlan plan, Guid originalId, CancellationToken ct)
    {
        var original = await db.EditPlans.AsNoTracking().SingleAsync(x => x.Id == originalId, ct);
        if (original.CampaignId != plan.CampaignId || original.SourceAssetId != plan.SourceAssetId)
            throw new InvalidOperationException("The saved mix belongs to a different clip.");
        var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)
            ?? throw new InvalidOperationException("The clip's saved settings are missing.");
        var previous = JsonDefaults.Parse<EditPlanExtrasDto?>(original.ExtraSettingsJson, null);
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", plan.CampaignId.ToString("N"),
            "output", plan.Platform.ToString().ToLowerInvariant()));
        var oldName = WorkspaceService.SafeFileName(Path.GetFileNameWithoutExtension(original.OutputFileName), "clip");
        var oldPath = Path.Combine(directory, $"{oldName}-{original.Id:N}.mp4");
        var voicePath = Path.Combine(directory, $"{oldName}-{original.Id:N}.preview.mp4");
        if (!File.Exists(voicePath) && !PodcastMusic.HasRenderedMusic(original.LastFfmpegArgumentsJson)) voicePath = oldPath;
        if (!File.Exists(oldPath) || !File.Exists(voicePath))
            throw new FileNotFoundException("The saved video or voice track needed for this mix is missing.");
        var oldVoice = PodcastMusic.RenderedVoiceVolume(original.LastFfmpegArgumentsJson, previous?.VoiceVolume ?? 1);
        var voice = extras.VoiceVolume ?? oldVoice;
        if (oldVoice <= 0 && voice > 0)
            throw new InvalidOperationException("A muted saved voice track cannot restore speech. Rebuild this clip from its source.");
        var duration = await ProbeDurationSecondsAsync(oldPath, ct);
        if (duration <= 0) throw new InvalidOperationException("The saved clip's duration could not be read.");
        string? songPath = null;
        var start = 0d;
        if (plan.MusicAssetId is { } musicId)
        {
            var song = await ResolveAudioAssetAsync(musicId, MediaAssetKind.Music, ct);
            songPath = song.FullPath;
            start = PodcastMusic.ResolveStart(extras.MusicStartSeconds, song.SegmentStartSeconds,
                await ProbeDurationSecondsAsync(songPath, ct));
        }
        var name = WorkspaceService.SafeFileName(Path.GetFileNameWithoutExtension(plan.OutputFileName), "clip");
        var finalPath = Path.Combine(directory, $"{name}-{plan.Id:N}.mp4");
        var stemPath = Path.Combine(directory, $"{name}-{plan.Id:N}.preview.mp4");
        var args = FfmpegArgumentBuilder.BuildPodcastRemix(oldPath, voicePath, songPath, finalPath, stemPath,
            duration, start, oldVoice > 0 ? voice / oldVoice : 0, extras.MusicVolume ?? plan.MusicVolume,
            extras.MusicDuckingDb, plan.NormalizeAudio);
        plan.LastFfmpegArgumentsJson = JsonSerializer.Serialize(args, JsonDefaults.Serializer);
        var result = await processRunner.RunAsync(new ProcessInvocation(options.Value.FfmpegPath, args,
            directory, Timeout: TimeSpan.FromMinutes(10)), ct);
        Capture(job, result);
        EnsureSuccess(result, "FFmpeg music-only remix");
        if (!File.Exists(finalPath) || !File.Exists(stemPath))
            throw new InvalidOperationException("The music-only render did not produce both the video and voice track.");
        plan.ExtraSettingsJson = JsonSerializer.Serialize(extras with
        {
            MusicStartSeconds = start, VoiceVolume = voice, MusicNeedsRender = false,
        }, JsonDefaults.Serializer);
        await db.SaveChangesAsync(ct);
        // Keep the previous cut recoverable and out of the active output gallery.
        var archive = workspace.ResolveRelative(Path.Combine("campaigns", plan.CampaignId.ToString("N"),
            "intermediate", "superseded-mixes", original.Id.ToString("N")));
        Directory.CreateDirectory(archive);
        foreach (var path in new[] { oldPath, voicePath }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            WorkspaceService.EnsureInside(directory, path);
            var target = Path.Combine(archive, Path.GetFileName(path));
            WorkspaceService.EnsureInside(archive, target);
            if (File.Exists(path) && !File.Exists(target)) File.Move(path, target);
        }
        job.ResultJson = JsonSerializer.Serialize(new
        {
            outputRelativePath = workspace.ToRelative(finalPath), editPlanId = plan.Id,
            styling = EditStyles.PodcastSplit, seconds = duration, musicOnly = true, replacedEditPlanId = original.Id,
        }, JsonDefaults.Serializer);
    }
}
