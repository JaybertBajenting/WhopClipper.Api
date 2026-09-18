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
        var split = plan is null ? null : JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
        if (split is null) return null;
        var footage = split.Broll
            .GroupBy(x => x.SourceUrl ?? x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => new RenderedClipFootageDto(x.First().Query, x.First().SourceTitle, x.First().SourceUrl))
            .ToList();
        return new RenderedClipStyleDto(EditStyles.PodcastSplit, split.Pieces.Count, split.TopShots.Count,
            split.Broll.Count, split.Gaps, footage);
    }

    internal static Dictionary<string, string[]> ValidateStyle(CampaignEditStyleDto style)
    {
        var errors = new Dictionary<string, string[]>();
        if (!EditStyles.IsKnown(style.Style)) errors["style"] = [$"Style must be {EditStyles.Streamer} or {EditStyles.PodcastSplit}."];
        if (style.MusicVolume is < 0 or > 1) errors["musicVolume"] = ["Music volume must be between 0 and 1."];
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
