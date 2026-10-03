using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

/// <param name="MusicAssetId">A different song from the music library; null keeps the clip's song.</param>
/// <param name="MusicVolume">The song's gain, 0 to 1.5; null keeps the clip's level.</param>
/// <param name="OverlayTitle">A new meme line for over the window; null keeps the clip's line.</param>
/// <param name="PayoffShiftX">Moves the moment's window sideways by this share of the frame's width, positive to the
/// right: for a vlog shot with something at the window's edge that must not show, such as an app's logo.</param>
/// <param name="Skull">False leaves the skull off the ending, keeping the black-and-white freeze; true puts it back.
/// Null keeps the clip as it is.</param>
public sealed record NarratedRerenderRequest(Guid? MusicAssetId = null, double? MusicVolume = null, string? OverlayTitle = null,
    double? PayoffShiftX = null, bool? Skull = null);

public static class NarratedEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // Renders a finished narrated clip again, over the same file. Its footage is fetched again and its narration
        // is reused, so a new song, level or title costs no AI call and no voice credits.
        api.MapPost("/edit-plans/{planId:guid}/narrated-rerender", async (Guid planId, NarratedRerenderRequest request,
            AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.SingleOrDefaultAsync(x => x.Id == planId, ct);
            if (plan is null) return Results.NotFound();
            var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
            if (extras is not { Framing: EditStyles.NarratedLuxury, Narrated: not null })
                return Results.BadRequest(new { error = "This clip is not in the narrated style." });
            if (request.MusicVolume is < 0 or > 1.5) return Results.BadRequest(new { error = "Music volume is 0 to 1.5." });
            if (request.PayoffShiftX is < -0.5 or > 0.5) return Results.BadRequest(new { error = "The sideways shift is -0.5 to 0.5 of the frame." });
            var title = request.OverlayTitle?.Trim();
            if (title is { Length: < 3 or > 70 }) return Results.BadRequest(new { error = "The line over the video must be 3 to 70 characters." });
            if (request.Skull == true && extras.Narrated.Skull is null)
                return Results.BadRequest(new { error = "This clip never had a skull: no face was found on its last frame." });
            if (request.MusicAssetId is { } musicId)
            {
                if (!await db.MediaLibraryAssets.AnyAsync(x => x.Id == musicId && x.Kind == MediaAssetKind.Music, ct))
                    return Results.BadRequest(new { error = "That song is not in the music library." });
                plan.MusicAssetId = musicId;
            }
            // A render already waiting or running for this clip would write the same file at the same time.
            var marker = $"\"editPlanId\":\"{plan.Id}\"";
            if (await db.Jobs.AnyAsync(x => x.Kind == JobKind.Render && (x.Status == JobStatus.Queued || x.Status == JobStatus.Running) &&
                                            x.PayloadJson.Contains(marker), ct))
                return Results.Conflict(new { error = "This clip is already being rendered." });
            var narrated = title is null ? extras.Narrated : extras.Narrated with { OverlayTitle = AiOutputParser.CleanHookText(title) };
            if (request.PayoffShiftX is { } shift && shift != 0)
                narrated = narrated with
                {
                    Shots = narrated.Shots.Select(x => x.Payoff ? x with { FocusX = NarratedStories.ShiftFocus(x.FocusX, shift) } : x).ToList(),
                };
            if (request.Skull is { } skull) narrated = narrated with { SkullOff = !skull };
            plan.ExtraSettingsJson = System.Text.Json.JsonSerializer.Serialize(extras with
            {
                MusicVolume = request.MusicVolume ?? extras.MusicVolume, Narrated = narrated,
            }, JsonDefaults.Serializer);
            await db.SaveChangesAsync(ct);
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.Render, new RenderJobPayload(plan.Id), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto(subjectName: extras.Narrated.Title));
        }).WithTags("Edit style");
    }
}
