using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

/// <summary>The campaign's edit style, and queueing hand-picked stories for the podcast-split edit.</summary>
public static partial class PodcastSplitEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var campaigns = api.MapGroup("/campaigns").WithTags("Edit style");

        campaigns.MapGet("/{id:guid}/edit-style", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return campaign is null ? Results.NotFound() : Results.Ok(campaign.ReadEditStyle() ?? new CampaignEditStyleDto());
        });

        // The story a podcast-split plan was built from, on the episode clock, so it can be re-imported
        // and re-rendered (e.g. with a different song) without the AI picking a different moment. The
        // plan's stored pieces are on the span clock; the episode clock is that plus the saved offset.
        api.MapGet("/edit-plans/{planId:guid}/story", async (Guid planId, AppDbContext db, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var split = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
            if (split is null) return Results.BadRequest(new { error = "This plan is not a podcast split." });
            var pieces = split.Pieces.Select(p => new StoryPieceDto(
                Math.Round(p.StartSeconds + split.EpisodeOffsetSeconds, 3),
                Math.Round(p.EndSeconds + split.EpisodeOffsetSeconds, 3))).ToList();
            var title = string.IsNullOrWhiteSpace(split.Title)
                ? System.IO.Path.GetFileNameWithoutExtension(plan.OutputFileName) : split.Title;
            return Results.Ok(new { plan.SourceAssetId, Title = title, Pieces = pieces, plan.MusicAssetId });
        }).WithTags("Edit style");

        // What the app's live preview needs for one clip: the music-free companion video, the song and
        // where it starts, and the clip's current voice/music levels (its own override, else campaign
        // default). The player plays the companion and mixes the song on top under the sliders.
        api.MapGet("/edit-plans/{planId:guid}/preview", async (Guid planId, AppDbContext db,
            WorkspaceService workspace, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
            if (extras?.PodcastSplit is null) return Results.BadRequest(new { error = "This plan is not a podcast split." });
            var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == plan.CampaignId, ct);
            var style = campaign?.ReadEditStyle() ?? new CampaignEditStyleDto();
            var name = WorkspaceService.SafeFileName(System.IO.Path.GetFileNameWithoutExtension(plan.OutputFileName), "clip");
            var previewAbs = workspace.ResolveRelative(System.IO.Path.Combine("campaigns", plan.CampaignId.ToString("N"),
                "output", plan.Platform.ToString().ToLowerInvariant(), $"{name}-{plan.Id:N}.preview.mp4"));
            return Results.Ok(new
            {
                campaignId = plan.CampaignId,
                previewRelativePath = System.IO.File.Exists(previewAbs) ? workspace.ToRelative(previewAbs) : null,
                musicAssetId = plan.MusicAssetId,
                musicStartSeconds = extras.MusicStartSeconds ?? 0,
                voiceVolume = extras.VoiceVolume ?? style.VoiceVolume,
                musicVolume = extras.MusicVolume ?? style.MusicVolume,
            });
        }).WithTags("Edit style");

        // Re-render one clip from its stored story, picking up the campaign's current voice/music
        // volumes. This is what the app's "Re-render" button calls after the sliders are adjusted.
        api.MapPost("/edit-plans/{planId:guid}/rerender", async (Guid planId, double? voiceVolume, double? musicVolume,
            AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (voiceVolume is < 0 or > 4 || musicVolume is < 0 or > 1)
                return Results.BadRequest(new { error = "Voice volume is 0-4 and music volume is 0-1." });
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
            var split = extras?.PodcastSplit;
            if (split is null) return Results.BadRequest(new { error = "This plan is not a podcast split." });
            var pieces = split.Pieces.Select(p => new StoryPieceDto(
                Math.Round(p.StartSeconds + split.EpisodeOffsetSeconds, 3),
                Math.Round(p.EndSeconds + split.EpisodeOffsetSeconds, 3))).ToList();
            var title = string.IsNullOrWhiteSpace(split.Title)
                ? System.IO.Path.GetFileNameWithoutExtension(plan.OutputFileName) : split.Title;
            // This clip replaces the one being re-rendered, so the old file is retired once the new one
            // renders - otherwise every tweak left another copy of the same moment in the list.
            var story = new ImportedStoryDto(title, pieces, plan.MusicAssetId, voiceVolume, musicVolume,
                SupersedesEditPlanId: plan.Id, Caption: extras?.Caption ?? string.Empty);
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.SourcePipeline,
                new SourcePipelineJobPayload(plan.SourceAssetId, plan.Platform, MaxClips: 1,
                    DeleteSourceWhenDone: false, Mode: AiMode.Auto, EditStyle: EditStyles.PodcastSplit,
                    Stories: new[] { story }), ct);
            return Results.Accepted("/api/jobs", job.ToDto());
        }).WithTags("Edit style");

        // Rewrite one clip's title and caption. For a podcast split these are metadata - the words on
        // screen are the seam captions, not the title - so this needs no re-render. The file keeps the
        // name it was rendered under; the title shown and copied comes from the plan.
        api.MapPut("/edit-plans/{planId:guid}/copy", async (Guid planId, SetPlanCopyRequest request,
            AppDbContext db, CancellationToken ct) =>
        {
            var title = request.Title?.Trim() ?? string.Empty;
            var caption = request.Caption?.Trim() ?? string.Empty;
            if (title.Length is 0 or > 200) return Results.BadRequest(new { error = "Title is 1-200 characters." });
            if (caption.Length > 2200) return Results.BadRequest(new { error = "Caption is at most 2200 characters." });
            var plan = await db.EditPlans.SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
            if (extras?.PodcastSplit is null) return Results.BadRequest(new { error = "This plan is not a podcast split." });
            plan.ExtraSettingsJson = JsonSerializer.Serialize(
                extras with { PodcastSplit = extras.PodcastSplit with { Title = title }, Caption = caption },
                JsonDefaults.Serializer);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { title, caption });
        }).WithTags("Edit style");

        // Point one clip at a specific bed without re-rendering it. Updates the plan's music id and the
        // stored start (the asset's own segment when the caller doesn't pass one), so the live preview and
        // any later re-render both use the new song. Used to give each highlight its own bed from the folder.
        api.MapPut("/edit-plans/{planId:guid}/music", async (Guid planId, SetPlanMusicRequest request,
            AppDbContext db, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
            if (extras?.PodcastSplit is null) return Results.BadRequest(new { error = "This plan is not a podcast split." });
            double? start = request.MusicStartSeconds;
            if (request.MusicAssetId is { } assetId)
            {
                var asset = await db.MediaLibraryAssets.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == assetId && x.Kind == MediaAssetKind.Music, ct);
                if (asset is null) return Results.BadRequest(new { error = "No such music asset." });
                start ??= asset.SegmentStartSeconds ?? 0;
            }
            else start = null;
            if (start is < 0) return Results.BadRequest(new { error = "Music start cannot be negative." });
            plan.MusicAssetId = request.MusicAssetId;
            plan.ExtraSettingsJson = JsonSerializer.Serialize(extras with { MusicStartSeconds = start }, JsonDefaults.Serializer);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { plan.MusicAssetId, musicStartSeconds = start });
        }).WithTags("Edit style");

        campaigns.MapPut("/{id:guid}/edit-style", async (Guid id, CampaignEditStyleDto request, AppDbContext db,
            CancellationToken ct) =>
        {
            var errors = ValidateStyle(request);
            // A campaign draws its beds from one folder, so one show's music cannot turn up under another's.
            if (request.MusicLocationId is { } locationId && !await db.MediaLibraryLocations
                    .AnyAsync(x => x.Id == locationId && x.Kind == MediaAssetKind.Music, ct))
                errors["musicLocationId"] = ["That music folder is not in the media library."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            campaign.EditStyleJson = JsonSerializer.Serialize(request, JsonDefaults.Serializer);
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(request);
        });

        // Builds exactly these stories from one episode with the podcast-split edit. Used to recreate a
        // reference short from the moments it was cut from, and for any story picked by reading the
        // transcript rather than left to the AI.
        campaigns.MapPost("/{id:guid}/stories", async (Guid id, ImportStoriesRequest request, AppDbContext db,
            JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var errors = ValidateStories(request);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            SourceAsset? source = null;
            if (request.SourceAssetId is { } sourceId)
                source = await db.SourceAssets.SingleOrDefaultAsync(x => x.Id == sourceId && x.CampaignId == id, ct);
            else if (!string.IsNullOrWhiteSpace(request.Url))
            {
                var url = request.Url.Trim();
                source = await db.SourceAssets.SingleOrDefaultAsync(x => x.CampaignId == id && x.Url == url, ct);
                if (source is null)
                {
                    source = new SourceAsset { CampaignId = id, Url = url };
                    db.SourceAssets.Add(source);
                    await db.SaveChangesAsync(ct);
                }
            }
            if (source is null) return Results.BadRequest(new { error = "Give the episode as a source id on this campaign or a video URL." });

            var job = await coordinator.CreateAsync(id, JobKind.SourcePipeline, new SourcePipelineJobPayload(source.Id,
                request.Platform, MaxClips: request.Stories.Count, DeleteSourceWhenDone: false, Mode: request.Mode,
                EditStyle: EditStyles.PodcastSplit, Stories: request.Stories), ct);
            return Results.Accepted("/api/jobs", job.ToDto(subjectName: source.Title ?? source.Url));
        });
    }

    /// <summary>Null for every clip that is not a podcast split, which keeps the listing unchanged for them.</summary>
    internal static RenderedClipStyleDto? DescribeClip(EditPlan? plan)
    {
        var extras = plan is null ? null : JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
        var split = extras?.PodcastSplit;
        if (split is null) return null;
        var footage = split.Broll
            .GroupBy(x => x.SourceUrl ?? x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => new RenderedClipFootageDto(x.First().Query, x.First().SourceTitle, x.First().SourceUrl))
            .ToList();
        return new RenderedClipStyleDto(EditStyles.PodcastSplit, split.Pieces.Count, split.TopShots.Count,
            split.Broll.Count, split.Gaps, footage, extras!.VoiceVolume, extras.MusicVolume);
    }

    internal static Dictionary<string, string[]> ValidateStyle(CampaignEditStyleDto style)
    {
        var errors = new Dictionary<string, string[]>();
        if (!EditStyles.IsKnown(style.Style)) errors["style"] = [$"Style must be {EditStyles.Streamer} or {EditStyles.PodcastSplit}."];
        if (style.MusicVolume is < 0 or > 1) errors["musicVolume"] = ["Music volume must be between 0 and 1."];
        if (style.VoiceVolume is < 0 or > 4) errors["voiceVolume"] = ["Voice volume must be between 0 and 4."];
        if (style.MusicDuckingDb is < -40 or > 0) errors["musicDuckingDb"] = ["Ducking must be between -40 and 0 dB."];
        if (style.CaptionFontSize is < 40 or > 160) errors["captionFontSize"] = ["Caption size must be 40-160."];
        // The name lands inside an ASS style line, where a comma would start the next field.
        if (string.IsNullOrWhiteSpace(style.CaptionFont) || style.CaptionFont.Length > 60 || !FontName().IsMatch(style.CaptionFont))
            errors["captionFont"] = ["Use a font name of letters, digits and spaces."];
        if (style.LogoWidthFraction is < 0.05 or > 0.6) errors["logoWidthFraction"] = ["Logo width must be 0.05-0.6 of the frame."];
        if (style.LogoRightMarginFraction is < 0 or > 0.45 || style.LogoTopMarginFraction is < 0 or > 0.45)
            errors["logoMargins"] = ["Logo margins must be 0-0.45 of the frame."];
        if (style.BrollShotSeconds is < 0.8 or > 6) errors["brollShotSeconds"] = ["B-roll shots must be 0.8-6 seconds."];
        return errors;
    }

    internal static Dictionary<string, string[]> ValidateStories(ImportStoriesRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Stories is not { Count: >= 1 and <= 40 })
        {
            errors["stories"] = ["Give between 1 and 40 stories."];
            return errors;
        }
        for (var index = 0; index < request.Stories.Count; index++)
        {
            var story = request.Stories[index];
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(story.Title) || story.Title.Length > 200) problems.Add("A title of up to 200 characters is required.");
            if (story.Pieces is not { Count: >= 1 and <= 20 }) problems.Add("Each story needs 1-20 pieces.");
            else
            {
                if (story.Pieces.Any(x => x.StartSeconds < 0 || x.EndSeconds <= x.StartSeconds))
                    problems.Add("Every piece must end after it starts.");
                var seconds = story.Pieces.Sum(x => Math.Max(0, x.EndSeconds - x.StartSeconds));
                if (seconds is < 3 or > 90) problems.Add("Pieces must add up to 3-90 seconds.");
                if (story.Pieces.Max(x => x.EndSeconds) - story.Pieces.Min(x => x.StartSeconds) > 900)
                    problems.Add("Pieces must come from within 15 minutes of each other; that is the most one download covers.");
            }
            if (problems.Count > 0) errors[$"stories[{index}]"] = problems.ToArray();
        }
        return errors;
    }

    [GeneratedRegex(@"^[A-Za-z0-9 ]+$")]
    private static partial Regex FontName();
}
