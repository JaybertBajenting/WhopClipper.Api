using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.App.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapWhopClipperApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        // The phone app's PC half is switched off unless WhopClipper:PhoneCompanion is set. The Publishing page asks
        // here whether to show its screen.
        var phoneCompanion = app.ServiceProvider.GetRequiredService<IOptions<AppOptions>>().Value.PhoneCompanion;
        api.MapGet("/publishing/phone-app/enabled", () => Results.Ok(new { enabled = phoneCompanion })).WithTags("Phone app");
        // Persist phone changes before completing schedule/campaign writes, with background retry
        // if Firebase is offline. Phone sync cannot turn a successful local edit into a failed edit.
        if (phoneCompanion) api.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            var request = context.HttpContext.Request;
            var path = request.Path.Value ?? "";
            var write = HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
                || HttpMethods.IsDelete(request.Method) || HttpMethods.IsPatch(request.Method);
            if (write && !path.StartsWith("/api/publishing/phone-app", StringComparison.Ordinal)
                && (path.StartsWith("/api/campaigns", StringComparison.Ordinal) || path.StartsWith("/api/publishing", StringComparison.Ordinal))
                && (result is not Microsoft.AspNetCore.Http.IStatusCodeHttpResult status || status.StatusCode is null or < 400))
                await context.HttpContext.RequestServices.GetRequiredService<PhoneCompanionService>().TrySyncAsync(request.HttpContext.RequestAborted);
            return result;
        });
        MapDashboard(api);
        MapCampaigns(api);
        MapJobs(api);
        MapAi(api);
        MapEditPlans(api);
        MapMediaLibrary(api);
        MapSettings(api);
        PublishingEndpoints.Map(api);
        if (phoneCompanion) PhoneCompanionEndpoints.Map(api);
        YouTubeScheduleEndpoints.Map(api);
        VoiceOverEndpoints.Map(api);
        NarratedEndpoints.Map(api);
        PodcastSplitEndpoints.Map(api);
        return app;
    }

    /// <summary>
    /// Finds the plan a rendered file came from. Podcast-split renders end in their plan id, which is exact.
    /// Matching by name alone took the oldest of three plans that shared a story title, so a Teofimo clip
    /// listed a cancelled render's footage. Other renders end in a job id and still match by name, newest first.
    /// </summary>
    internal static EditPlan? MatchPlanForOutput(IReadOnlyList<EditPlan> plans, string stem) =>
        plans.FirstOrDefault(x => stem.EndsWith(x.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
        ?? plans.Where(x => stem.StartsWith(Path.GetFileNameWithoutExtension(x.OutputFileName), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();

    private static void MapDashboard(RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var allCampaigns = await db.Campaigns.AsNoTracking().Include(x => x.Sources).Include(x => x.PlatformProfiles)
                .Include(x => x.RequirementRevisions).OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
            var recentJobs = await db.Jobs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(8).ToListAsync(ct);
            var names = allCampaigns.ToDictionary(x => x.Id, x => x.Name);
            var counts = new CampaignCountsDto(allCampaigns.Count, allCampaigns.Count(x => x.Status == CampaignStatus.Draft),
                allCampaigns.Count(x => x.Status == CampaignStatus.Ready), allCampaigns.Count(x => x.Status == CampaignStatus.Processing),
                allCampaigns.Count(x => x.Status == CampaignStatus.NeedsReview), allCampaigns.Count(x => x.Status == CampaignStatus.Completed),
                allCampaigns.Count(x => x.Status == CampaignStatus.Archived));
            var activeJobs = await db.Jobs.CountAsync(x => x.Status == JobStatus.Queued || x.Status == JobStatus.Running, ct);
            var failedJobs = await db.Jobs.CountAsync(x => x.Status == JobStatus.Failed, ct);
            return Results.Ok(new DashboardDto(counts, activeJobs, failedJobs, counts.NeedsReview,
                workspace.GetStorageSizeBytes(), allCampaigns.Where(x => x.Status != CampaignStatus.Archived).Take(6).Select(x => x.ToSummary()).ToList(),
                recentJobs.Select(x => x.ToDto(x.CampaignId.HasValue && names.TryGetValue(x.CampaignId.Value, out var name) ? name : null)).ToList(),
                allCampaigns.Count(x => x.Status != CampaignStatus.Archived), counts.Ready, counts.NeedsReview, failedJobs));
        }).WithTags("Dashboard");
    }

    private static void MapCampaigns(RouteGroupBuilder api)
    {
        var campaigns = api.MapGroup("/campaigns").WithTags("Campaigns");
        campaigns.MapGet("/", async (string? search, bool? includeArchived, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Campaigns.AsNoTracking().Include(x => x.Sources).Include(x => x.PlatformProfiles)
                .Include(x => x.RequirementRevisions).AsQueryable();
            if (includeArchived != true) query = query.Where(x => x.Status != CampaignStatus.Archived);
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.ToLower().Contains(search.ToLower()));
            return Results.Ok((await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct)).Select(x => x.ToSummary()));
        });

        campaigns.MapPost("/", async (CreateCampaignRequest request, IValidator<CreateCampaignRequest> validator,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            var campaign = new Campaign
            {
                Name = request.Name.Trim(), WhopUrl = request.WhopUrl, CampaignType = request.CampaignType ?? "Clipping",
                Brand = request.Brand, Category = request.Category, PayoutAmount = request.PayoutAmount,
                PayoutPerViews = request.PayoutPerViews, PayoutUnit = request.PayoutUnit, Deadline = request.Deadline,
                Notes = request.Notes, RawInstructions = request.RawInstructions ?? string.Empty
            };
            db.Campaigns.Add(campaign);
            await db.SaveChangesAsync(ct);
            workspace.CampaignDirectory(campaign.Id);
            return Results.Created($"/api/campaigns/{campaign.Id}", campaign.ToDetail());
        });

        campaigns.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var campaign = await DetailQuery(db).SingleOrDefaultAsync(x => x.Id == id, ct);
            return campaign is null ? Results.NotFound() : Results.Ok(campaign.ToDetail());
        });

        campaigns.MapPut("/{id:guid}", async (Guid id, UpdateCampaignRequest request, IValidator<UpdateCampaignRequest> validator,
            AppDbContext db, YouTubeScheduleService schedule, CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            // Phone notifications that are already set carry this campaign's name and link.
            var notified = (campaign.Name, campaign.WhopUrl);
            campaign.Name = request.Name.Trim(); campaign.WhopUrl = request.WhopUrl; campaign.CampaignType = request.CampaignType ?? "Clipping";
            campaign.Brand = request.Brand; campaign.Category = request.Category; campaign.PayoutAmount = request.PayoutAmount;
            campaign.PayoutPerViews = request.PayoutPerViews; campaign.PayoutUnit = request.PayoutUnit; campaign.Deadline = request.Deadline;
            campaign.Notes = request.Notes; campaign.RawInstructions = request.RawInstructions ?? string.Empty;
            campaign.Status = request.Status; campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            if (notified != (campaign.Name, campaign.WhopUrl)) await schedule.RefreshNotificationsAsync(id, ct);
            return Results.Ok((await DetailQuery(db).SingleAsync(x => x.Id == id, ct)).ToDetail());
        });

        // Archiving is the default and is reversible. `?purge=true` is the destructive
        // variant: it removes the rows and the campaign's workspace folder for good.
        campaigns.MapDelete("/{id:guid}", async (Guid id, bool? purge, AppDbContext db, WorkspaceService workspace,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            if (purge != true)
            {
                campaign.Status = CampaignStatus.Archived; campaign.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }

            if (await db.Jobs.AnyAsync(x => x.CampaignId == id && (x.Status == JobStatus.Queued || x.Status == JobStatus.Running), ct))
                return Results.Conflict(new { error = "Cancel this campaign's running jobs before deleting it." });

            // EditPlans, AiAnalysisRuns, and Jobs reference the campaign by id without a
            // navigation, so no cascade covers them.
            db.EditPlans.RemoveRange(await db.EditPlans.Where(x => x.CampaignId == id).ToListAsync(ct));
            db.AiAnalysisRuns.RemoveRange(await db.AiAnalysisRuns.Include(x => x.Highlights).Where(x => x.CampaignId == id).ToListAsync(ct));
            db.Jobs.RemoveRange(await db.Jobs.Where(x => x.CampaignId == id).ToListAsync(ct));
            db.Campaigns.Remove(campaign);
            await db.SaveChangesAsync(ct);

            var logger = loggerFactory.CreateLogger("CampaignDeletion");
            try
            {
                var directory = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N")));
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Campaign {CampaignId} was deleted but its workspace folder could not be removed.", id);
                return Results.Ok(new { deleted = true, workspaceRemoved = false, warning = "The campaign was deleted, but its workspace folder could not be removed. Delete it manually if you no longer need it." });
            }
            return Results.Ok(new { deleted = true, workspaceRemoved = true });
        });

        campaigns.MapPost("/{id:guid}/restore", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var campaign = await DetailQuery(db).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            if (campaign.Status != CampaignStatus.Archived)
                return Results.Conflict(new { error = "Only archived campaigns can be restored." });
            campaign.Status = campaign.RequirementRevisions.Any(x => x.IsApproved) ? CampaignStatus.Ready : CampaignStatus.Draft;
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(campaign.ToDetail());
        });

        campaigns.MapPost("/{id:guid}/duplicate", async (Guid id, AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var original = await DetailQuery(db).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (original is null) return Results.NotFound();
            var copy = new Campaign
            {
                Name = original.Name + " (copy)", WhopUrl = original.WhopUrl, CampaignType = original.CampaignType,
                Brand = original.Brand, Category = original.Category, PayoutAmount = original.PayoutAmount,
                PayoutPerViews = original.PayoutPerViews, PayoutUnit = original.PayoutUnit, Deadline = original.Deadline,
                RawInstructions = original.RawInstructions,
                Notes = original.Notes, Status = CampaignStatus.Draft
            };
            foreach (var profile in original.PlatformProfiles) copy.PlatformProfiles.Add(CloneProfile(profile));
            foreach (var source in original.Sources) copy.Sources.Add(new SourceAsset { Url = source.Url, Title = source.Title, Uploader = source.Uploader, DurationSeconds = source.DurationSeconds, ThumbnailUrl = source.ThumbnailUrl, MetadataJson = source.MetadataJson });
            // URL-backed references travel with the copy and re-analyze on demand; uploaded reference media is not duplicated.
            foreach (var reference in original.ReferenceVideos.Where(x => !string.IsNullOrWhiteSpace(x.Url)))
                copy.ReferenceVideos.Add(new ReferenceVideo { Label = reference.Label, Url = reference.Url, Notes = reference.Notes, IsPrimary = reference.IsPrimary });
            db.Campaigns.Add(copy); await db.SaveChangesAsync(ct);
            workspace.CampaignDirectory(copy.Id);
            foreach (var document in original.Documents) await CopyDocumentAsync(db, workspace, copy.Id, document, ct);
            if (original.Documents.Count > 0) await db.SaveChangesAsync(ct);
            return Results.Created($"/api/campaigns/{copy.Id}", (await DetailQuery(db).SingleAsync(x => x.Id == copy.Id, ct)).ToDetail());
        });

        campaigns.MapPost("/{id:guid}/extract-requirements", async (Guid id, RequirementService service, CancellationToken ct) =>
        {
            var revision = await service.ExtractAsync(id, ct);
            return revision is null ? Results.NotFound() : Results.Ok(new RequirementRevisionDto(revision.Id, revision.Version,
                revision.IsApproved, revision.CreatedAt, revision.ApprovedAt, revision.Items.Select(x => new RequirementItemDto(x.Id,
                    x.Category, x.Key, x.Value, x.SourceText, x.Confidence, x.NeedsReview)).ToList()));
        });

        campaigns.MapPut("/{id:guid}/requirement-revisions/{revisionId:guid}", async (Guid id, Guid revisionId,
            UpdateRequirementRevisionRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (request.Items is null || request.Items.Count == 0 || request.Items.Count > 500 || request.Items.Any(x => string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Value)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Provide between 1 and 500 items with a key and value."] });
            var revision = await db.RequirementRevisions.Include(x => x.Items)
                .SingleOrDefaultAsync(x => x.Id == revisionId && x.CampaignId == id, ct);
            if (revision is null) return Results.NotFound();
            if (revision.IsApproved) return Results.Conflict(new { error = "Approved revisions are immutable. Extract a new revision to make later changes." });
            db.RequirementItems.RemoveRange(revision.Items);
            revision.Items = request.Items.Select(x => new RequirementItem
            {
                Category = x.Category.Trim(), Key = x.Key.Trim(), Value = x.Value.Trim(), SourceText = x.SourceText,
                Confidence = Math.Clamp(x.Confidence, 0, 1), NeedsReview = x.NeedsReview
            }).ToList();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RequirementRevisionDto(revision.Id, revision.Version, revision.IsApproved, revision.CreatedAt,
                revision.ApprovedAt, revision.Items.Select(x => new RequirementItemDto(x.Id, x.Category, x.Key, x.Value,
                    x.SourceText, x.Confidence, x.NeedsReview)).ToList()));
        });

        campaigns.MapPost("/{id:guid}/requirement-revisions/{revisionId:guid}/approve", async (Guid id, Guid revisionId,
            RequirementService service, CancellationToken ct) =>
        {
            var revision = await service.ApproveAsync(id, revisionId, ct);
            return revision is null ? Results.NotFound() : Results.Ok(new { revision.Id, revision.Version, revision.IsApproved, revision.ApprovedAt });
        });

        campaigns.MapGet("/{id:guid}/platform-profiles/{platform}", async (Guid id, PlatformKind platform, AppDbContext db, CancellationToken ct) =>
        {
            var profile = await db.PlatformProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.CampaignId == id && x.Platform == platform, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile.ToDto());
        });

        campaigns.MapPut("/{id:guid}/platform-profiles/{platform}", async (Guid id, PlatformKind platform,
            UpdatePlatformProfileRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (request.Width is < 240 or > 7680 || request.Height is < 240 or > 7680 || request.MinDurationSeconds < 0 ||
                request.MaxDurationSeconds <= request.MinDurationSeconds)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = ["Dimensions or durations are invalid."] });
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var profile = await db.PlatformProfiles.SingleOrDefaultAsync(x => x.CampaignId == id && x.Platform == platform, ct);
            if (profile is null) { profile = new PlatformProfile { CampaignId = id, Platform = platform }; db.PlatformProfiles.Add(profile); }
            profile.Enabled = request.Enabled; profile.Width = request.Width; profile.Height = request.Height;
            profile.MinDurationSeconds = request.MinDurationSeconds; profile.PreferredDurationSeconds = request.PreferredDurationSeconds;
            profile.MaxDurationSeconds = request.MaxDurationSeconds; profile.MaxFileSizeBytes = request.MaxFileSizeBytes;
            profile.CaptionTemplate = request.CaptionTemplate ?? string.Empty; profile.RequiredHashtagsJson = JsonDefaults.StringArray(request.RequiredHashtags ?? []);
            profile.RequiredMentionsJson = JsonDefaults.StringArray(request.RequiredMentions ?? []); profile.DisclosureText = request.DisclosureText;
            profile.CallToAction = request.CallToAction; profile.BurnSubtitles = request.BurnSubtitles; profile.SafeAreaNotes = request.SafeAreaNotes;
            profile.ComplianceChecklistJson = JsonDefaults.StringArray(request.ComplianceChecklist ?? []);
            profile.ValueOriginsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["all"] = "userOverride" }, JsonDefaults.Serializer);
            profile.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
            return Results.Ok(profile.ToDto());
        });

        campaigns.MapGet("/{id:guid}/sources", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok(SourceDates.NewestFirst(await db.SourceAssets.AsNoTracking().Where(x => x.CampaignId == id).ToListAsync(ct)).Select(x => x.ToDto())));

        campaigns.MapPost("/{id:guid}/sources", async (Guid id, AddSourceRequest request, IValidator<AddSourceRequest> validator,
            AppDbContext db, CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var existing = await db.SourceAssets.SingleOrDefaultAsync(x => x.CampaignId == id && x.Url == request.Url, ct);
            if (existing is not null) return Results.Ok(existing.ToDto());
            var source = new SourceAsset { CampaignId = id, Url = request.Url.Trim() };
            db.SourceAssets.Add(source); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/campaigns/{id}/sources/{source.Id}", source.ToDto());
        });

        // Footage the user downloaded themselves. Files are matched to the source rows they
        // belong to by video id, so a manual download slots into the campaign like any other.
        campaigns.MapGet("/{id:guid}/sources/local-folder", async (Guid id, AppDbContext db, WorkspaceService workspace,
            CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var folder = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "source"));
            Directory.CreateDirectory(folder);
            return Results.Ok(new { folder });
        });

        // Hand over a list of videos and walk away: each is downloaded, transcribed, cut into
        // clips and then deleted, one at a time, leaving only the clips behind. Queued rather
        // than run here, because a single three-hour VOD is an hour of work on its own.
        campaigns.MapPost("/{id:guid}/pipelines", async (Guid id, StartSourcePipelineRequest request,
            AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var urls = (request.Urls ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (request.SourceAssetIds is { Count: 0 } && urls.Count == 0)
                return Results.BadRequest(new { error = "Give at least one source id or URL." });
            if (!PipelineFocus.IsKnown(request.Focus))
                return Results.BadRequest(new { error = "The only focus a run can have is \"cars\"." });
            // Car clips are a way of cutting the narrated style; any other style would ignore the focus and run as usual.
            if (PipelineFocus.Normalise(request.Focus) is not null &&
                JsonDefaults.Parse<CampaignEditStyleDto?>(
                    await db.Campaigns.Where(x => x.Id == id).Select(x => x.EditStyleJson).SingleAsync(ct), null)
                    is not { Style: EditStyles.NarratedLuxury })
                return Results.BadRequest(new { error = "Car clips are made in the narrated style, and this campaign uses another one." });

            var sourceIds = new List<Guid>(request.SourceAssetIds ?? []);
            foreach (var url in urls)
            {
                var existing = await db.SourceAssets.SingleOrDefaultAsync(x => x.CampaignId == id && x.Url == url, ct);
                if (existing is null)
                {
                    existing = new SourceAsset { CampaignId = id, Url = url };
                    db.SourceAssets.Add(existing);
                    await db.SaveChangesAsync(ct);
                }
                if (!sourceIds.Contains(existing.Id)) sourceIds.Add(existing.Id);
            }

            var known = await db.SourceAssets.Where(x => x.CampaignId == id && sourceIds.Contains(x.Id))
                .Select(x => x.Id).ToListAsync(ct);
            var missing = sourceIds.Except(known).ToList();
            if (missing.Count > 0) return Results.BadRequest(new { error = "Unknown sources.", sourceAssetIds = missing });

            var queued = new List<JobDto>();
            foreach (var sourceId in sourceIds)
            {
                var job = await coordinator.CreateAsync(id, JobKind.SourcePipeline,
                    new SourcePipelineJobPayload(sourceId, request.Platform, request.MaxClips,
                        request.DeleteSourceWhenDone, request.Mode, request.Quality,
                        request.Framing ?? "SmartCrop", request.HookYFraction,
                        Focus: PipelineFocus.Normalise(request.Focus)), ct);
                queued.Add(job.ToDto());
            }
            return Results.Accepted($"/api/jobs", queued);
        }).WithTags("Sources");

        // Working the whole clip bank without listing every source by hand. Each source is still
        // its own queued pipeline, so one that fails - no disk, a rate limit that outlasts its
        // retries - takes only itself down and the rest carry on.
        campaigns.MapPost("/{id:guid}/auto-run", async (Guid id, AutoRunCampaignRequest? request,
            AppDbContext db, JobCoordinator coordinator, DiskSpaceGuard diskSpace, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var settings = request ?? new AutoRunCampaignRequest();

            // Having clips is the honest test of "already done". The pipeline never sets
            // HighlightCandidates.IsSelected, so that flag says nothing about what was produced.
            var withoutClips = await db.SourceAssets
                .Where(x => x.CampaignId == id && !db.EditPlans.Any(p => p.SourceAssetId == x.Id))
                .OrderBy(x => x.DurationSeconds ?? 0)
                .ToListAsync(ct);

            // A known duration is proof yt-dlp resolved this URL to one video. Campaigns collect
            // the brand's channel, TikTok and Instagram profile links as sources too, and without
            // this an unattended run would hand a channel page to the downloader as though it were
            // a VOD - twelve queued sources where three were real. They are reported, not dropped
            // silently: inspecting one fills in the duration and the next run picks it up.
            var outstanding = withoutClips.Where(x => x.DurationSeconds is > 0).ToList();
            var notInspected = withoutClips.Where(x => x.DurationSeconds is null or <= 0)
                .Select(x => x.Title ?? x.Url ?? x.Id.ToString()).ToList();
            var skipped = await db.SourceAssets.CountAsync(x => x.CampaignId == id, ct) - withoutClips.Count;
            if (settings.MaxSources is { } cap && cap > 0) outstanding = outstanding.Take(cap).ToList();

            var queued = new List<JobDto>();
            if (!settings.DryRun)
            {
                foreach (var source in outstanding)
                {
                    var job = await coordinator.CreateAsync(id, JobKind.SourcePipeline,
                        new SourcePipelineJobPayload(source.Id, settings.Platform, settings.MaxClips,
                            settings.DeleteSourceWhenDone, settings.Mode, settings.Quality,
                            settings.Framing ?? "SmartCrop", settings.HookYFraction), ct);
                    queued.Add(job.ToDto());
                }
            }

            // Reported rather than enforced here: the queue runs one source at a time and deletes
            // each before the next, so the run only needs room for the largest, not for the sum.
            var estimate = outstanding.Count == 0
                ? 0
                : outstanding.Max(x => diskSpace.Estimate(x.DurationSeconds, settings.Quality));
            var names = outstanding
                .Select(x => x.Title ?? x.Url ?? x.Id.ToString())
                .ToList();
            return Results.Accepted("/api/jobs",
                new AutoRunCampaignDto(queued, names, notInspected, skipped, estimate,
                    diskSpace.Available, settings.DryRun));
        }).WithTags("Sources");

        campaigns.MapPost("/{id:guid}/sources/scan-local", async (Guid id, AppDbContext db, WorkspaceService workspace,
            LocalSourceScanner scanner, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var result = await scanner.ScanAsync(id, ct);
            var folder = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "source"));
            return Results.Ok(new LocalSourceScanDto(result.Attached, result.Added, result.AlreadyKnown, folder, result.Notes));
        });

        campaigns.MapGet("/{id:guid}/reference-videos/local-folder", async (Guid id, AppDbContext db,
            WorkspaceService workspace, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var folder = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "reference"));
            Directory.CreateDirectory(folder);
            return Results.Ok(new { folder });
        });

        campaigns.MapPost("/{id:guid}/reference-videos/scan-local", async (Guid id, AppDbContext db,
            WorkspaceService workspace, LocalSourceScanner scanner, JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var (result, toAnalyze) = await scanner.ScanReferencesAsync(id, ct);
            foreach (var referenceId in toAnalyze)
                await coordinator.CreateAsync(id, JobKind.ReferenceAnalysis, new ReferenceJobPayload(referenceId), ct);
            var folder = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "reference"));
            return Results.Ok(new LocalSourceScanDto(result.Attached, result.Added, result.AlreadyKnown, folder, result.Notes));
        });

        // Sources could be added but never removed. Downloaded footage is kept by default,
        // since it is expensive to re-fetch and can be re-registered with a folder scan.
        campaigns.MapDelete("/{id:guid}/sources/{sourceId:guid}", async (Guid id, Guid sourceId, bool? deleteFile,
            AppDbContext db, WorkspaceService workspace, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var source = await db.SourceAssets.SingleOrDefaultAsync(x => x.Id == sourceId && x.CampaignId == id, ct);
            if (source is null) return Results.NotFound();
            if (await db.Jobs.AnyAsync(x => x.CampaignId == id && (x.Status == JobStatus.Queued || x.Status == JobStatus.Running), ct))
                return Results.Conflict(new { error = "Cancel this campaign's running jobs before removing a source." });

            // Edit plans and analyses are meaningless once their source is gone.
            var plans = await db.EditPlans.Where(x => x.SourceAssetId == sourceId).ToListAsync(ct);
            var runs = await db.AiAnalysisRuns.Include(x => x.Highlights).Where(x => x.SourceAssetId == sourceId).ToListAsync(ct);
            db.EditPlans.RemoveRange(plans);
            db.AiAnalysisRuns.RemoveRange(runs);
            db.SourceAssets.Remove(source);
            await db.SaveChangesAsync(ct);

            var fileRemoved = false;
            if (deleteFile == true && !string.IsNullOrWhiteSpace(source.LocalRelativePath))
            {
                try
                {
                    var full = workspace.ResolveRelative(source.LocalRelativePath);
                    if (File.Exists(full)) { File.Delete(full); fileRemoved = true; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    loggerFactory.CreateLogger("SourceDeletion")
                        .LogWarning(ex, "Source {SourceId} was removed but its file could not be deleted.", sourceId);
                }
            }
            return Results.Ok(new { deleted = true, fileRemoved, editPlansRemoved = plans.Count, analysesRemoved = runs.Count });
        });

        campaigns.MapPost("/{id:guid}/sources/inspect", async (Guid id, InspectSourceRequest request,
            IValidator<AddSourceRequest> validator, AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var invalid = await Validate(new AddSourceRequest(request.Url), validator, ct); if (invalid is not null) return invalid;
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var source = await db.SourceAssets.SingleOrDefaultAsync(x => x.CampaignId == id && x.Url == request.Url, ct);
            if (source is null) { source = new SourceAsset { CampaignId = id, Url = request.Url.Trim() }; db.SourceAssets.Add(source); await db.SaveChangesAsync(ct); }
            var job = await coordinator.CreateAsync(id, JobKind.InspectSource, new SourceJobPayload(source.Id), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new { source = source.ToDto(), job = job.ToDto() });
        });

        // Word-level transcription. Downloaded captions are sentence-chunked, so they cannot
        // drive one-word captions; faster-whisper supplies a timestamp per word.
        campaigns.MapPost("/{id:guid}/sources/{sourceId:guid}/transcribe", async (Guid id, Guid sourceId,
            bool? refresh, AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var source = await db.SourceAssets.SingleOrDefaultAsync(x => x.Id == sourceId && x.CampaignId == id, ct);
            if (source is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(source.LocalRelativePath))
                return Results.Conflict(new { error = "Download or register the source video before transcribing it." });
            var job = await coordinator.CreateAsync(id, JobKind.Transcribe, new TranscribeJobPayload(sourceId, refresh == true), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });

        campaigns.MapPost("/{id:guid}/downloads", async (Guid id, StartDownloadRequest request, AppDbContext db,
            JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await db.SourceAssets.AnyAsync(x => x.Id == request.SourceAssetId && x.CampaignId == id, ct)) return Results.NotFound();
            var job = await coordinator.CreateAsync(id, JobKind.Download, new SourceJobPayload(request.SourceAssetId, request.Refresh, request.Quality), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });

        campaigns.MapPost("/{id:guid}/ai-analysis", async (Guid id, StartAiAnalysisRequest request, AppDbContext db,
            JobCoordinator coordinator, IOptions<AppOptions> options, CancellationToken ct) =>
        {
            if (!await db.SourceAssets.AnyAsync(x => x.Id == request.SourceAssetId && x.CampaignId == id, ct)) return Results.NotFound();
            var pendingJobId = Guid.NewGuid();
            var mode = request.Mode ?? options.Value.DefaultAiMode;
            var run = new AiAnalysisRun { CampaignId = id, SourceAssetId = request.SourceAssetId, JobId = pendingJobId, Mode = mode };
            var job = new JobRecord
            {
                Id = pendingJobId, CampaignId = id, Kind = JobKind.AiAnalysis,
                PayloadJson = JsonSerializer.Serialize(new AiJobPayload(run.Id, request.SourceAssetId, mode, request.Platform, request.Transcript), JsonDefaults.Serializer)
            };
            db.AiAnalysisRuns.Add(run); db.Jobs.Add(job); await db.SaveChangesAsync(ct);
            await coordinator.EnqueueExistingAsync(job.Id, ct);
            return Results.Accepted($"/api/ai-analysis/{run.Id}", new { analysisRunId = run.Id, job = job.ToDto() });
        });

        campaigns.MapGet("/{id:guid}/edit-plans", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.EditPlans.AsNoTracking().Where(x => x.CampaignId == id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct)).Select(x => x.ToDto())));

        campaigns.MapPost("/{id:guid}/edit-plans", async (Guid id, CreateEditPlanRequest request,
            IValidator<CreateEditPlanRequest> validator, AppDbContext db, CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            if (!await db.SourceAssets.AnyAsync(x => x.Id == request.SourceAssetId && x.CampaignId == id, ct)) return Results.NotFound();
            var profile = await db.PlatformProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.CampaignId == id && x.Platform == request.Platform, ct);
            var plan = new EditPlan
            {
                CampaignId = id, SourceAssetId = request.SourceAssetId, Platform = request.Platform,
                StartSeconds = request.StartSeconds, EndSeconds = request.EndSeconds,
                Width = request.Width ?? profile?.Width ?? 1080, Height = request.Height ?? profile?.Height ?? 1920,
                FramesPerSecond = request.FramesPerSecond ?? 30, Crf = request.Crf ?? 20,
                NormalizeAudio = request.NormalizeAudio, BurnSubtitles = request.BurnSubtitles,
                SubtitleRelativePath = request.SubtitleRelativePath, MusicAssetId = request.MusicAssetId,
                MusicVolume = request.MusicVolume ?? .12, OutputFileName = request.OutputFileName ?? $"{request.Platform.ToString().ToLowerInvariant()}-clip.mp4",
                IsApproved = request.IsApproved,
                ExtraSettingsJson = JsonSerializer.Serialize(new EditPlanExtrasDto(request.Framing ?? "CenterCrop", request.CtaOverlayText,
                    request.SubtitleStyle, request.MusicDuckingDb ?? -12, request.SoundEffects ?? [], request.Overlays ?? [],
                    request.ColorFlashes, request.ZoomPunches), JsonDefaults.Serializer)
            };
            db.EditPlans.Add(plan); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/edit-plans/{plan.Id}", plan.ToDto());
        });

        // Links already written in the brief should not have to be retyped. Detection reads
        // the same composed text extraction uses, and only ever proposes candidates.
        campaigns.MapGet("/{id:guid}/detected-links", async (Guid id, AppDbContext db, BriefLinkDetector detector,
            CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.AsNoTracking().Include(x => x.Documents).Include(x => x.Sources)
                .Include(x => x.ReferenceVideos).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            var text = RequirementSourceComposer.Compose(campaign.RawInstructions, campaign.Documents);
            var links = detector.Detect(text,
                campaign.Sources.Select(x => x.Url),
                campaign.ReferenceVideos.Select(x => x.Url ?? string.Empty).Where(x => x.Length > 0));
            return Results.Ok(links.Select(x => new DetectedLinkDto(x.Url, x.Host, x.Kind, x.Context, x.Occurrences,
                x.AlreadySource, x.AlreadyReference)));
        });

        // Accepting detected links in one action. The permission acknowledgement is still
        // required, but it is confirmed once for the batch instead of per URL.
        campaigns.MapPost("/{id:guid}/detected-links/import", async (Guid id, ImportLinksRequest request,
            AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (request.Urls is null || request.Urls.Count == 0 || request.Urls.Count > 50)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["urls"] = ["Provide between 1 and 50 URLs."] });
            var target = request.Target?.Trim().ToLowerInvariant();
            if (target is not ("source" or "reference"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = ["Target must be \"source\" or \"reference\"."] });
            if (!request.ConfirmedPermission)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmedPermission"] = ["Confirm you are allowed to download this media before importing it."] });

            var added = 0;
            var skipped = 0;
            var queued = 0;
            var errors = new List<string>();
            foreach (var raw in request.Urls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var url = raw?.Trim() ?? string.Empty;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    errors.Add($"{url}: only absolute http(s) URLs are accepted.");
                    continue;
                }
                // Only footage is ever fetched. A brand kit, Discord invite, or campaign page
                // in the brief is not something this app should download.
                if (BriefLinkDetector.Classify(url) != "Media")
                {
                    errors.Add($"{BriefLinkDetector.HostOf(url)}: not a video link, so it was not imported.");
                    continue;
                }
                if (target == "source")
                {
                    if (await db.SourceAssets.AnyAsync(x => x.CampaignId == id && x.Url == url, ct)) { skipped++; continue; }
                    var source = new SourceAsset { CampaignId = id, Url = url };
                    db.SourceAssets.Add(source);
                    await db.SaveChangesAsync(ct);
                    await coordinator.CreateAsync(id, JobKind.InspectSource, new SourceJobPayload(source.Id), ct);
                    // A Drive folder has nothing to download until it is expanded, and queuing one
                    // anyway is how a single link turned into a wall of identical failures. The
                    // inspect job replaces it with the videos inside, which are downloadable.
                    var isDriveFolder = GoogleDriveClient.TryParse(url) is { Kind: DriveTargetKind.Folder };
                    // The brief lists this footage to be clipped, so fetch it rather than
                    // leaving the user to press download on every row afterwards.
                    if (request.Download && !isDriveFolder)
                    {
                        await coordinator.CreateAsync(id, JobKind.Download, new SourceJobPayload(source.Id), ct);
                        queued++;
                    }
                }
                else
                {
                    if (await db.ReferenceVideos.AnyAsync(x => x.CampaignId == id && x.Url == url, ct)) { skipped++; continue; }
                    var reference = new ReferenceVideo
                    {
                        CampaignId = id, Url = url, Label = DefaultReferenceLabel(null, url),
                        IsPrimary = !await db.ReferenceVideos.AnyAsync(x => x.CampaignId == id, ct)
                    };
                    db.ReferenceVideos.Add(reference);
                    await db.SaveChangesAsync(ct);
                    // Reference analysis downloads the clip at 720p and measures its style.
                    await coordinator.CreateAsync(id, JobKind.ReferenceAnalysis, new ReferenceJobPayload(reference.Id), ct);
                    queued++;
                }
                added++;
            }
            return Results.Ok(new ImportLinksResult(added, skipped, queued, errors));
        });

        campaigns.MapGet("/{id:guid}/documents", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.CampaignDocuments.AsNoTracking().Where(x => x.CampaignId == id)
                .OrderBy(x => x.UploadedAt).ToListAsync(ct)).Select(x => x.ToDto())));

        // The raw request body is streamed straight to disk so a brief never has to fit a multipart form limit.
        campaigns.MapPost("/{id:guid}/documents", async (Guid id, string? fileName, HttpContext context, AppDbContext db,
            CampaignDocumentService documents, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(fileName) || !CampaignDocumentService.TryResolveKind(fileName, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["fileName"] = [$"Unsupported file type. Accepted formats: {CampaignDocumentService.AcceptedExtensions}."] });
            if (context.Request.ContentLength is > CampaignDocumentService.MaxUploadBytes)
                return DocumentTooLarge();
            AllowRequestBodySize(context, CampaignDocumentService.MaxUploadBytes + 8_192);
            try
            {
                var stored = await documents.StoreAsync(id, fileName, context.Request.Body, ct);
                db.CampaignDocuments.Add(stored.Document);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/campaigns/{id}/documents/{stored.Document.Id}",
                    new { document = stored.Document.ToDto(), warning = stored.Warning });
            }
            catch (BadHttpRequestException) { return DocumentTooLarge(); }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
            }
        });

        // Campaign briefs are not always a file. Reading the page's markup rather than a screenshot
        // of it keeps every link exactly as published - a Drive folder id mixing 1, l and I cannot
        // survive OCR, and one wrong character loses the campaign's footage - and captures sections
        // that were never scrolled into view. What comes back is inert data, never instructions.
        campaigns.MapPost("/{id:guid}/documents/fetch", async (Guid id, FetchBriefPageRequest request,
            AppDbContext db, CampaignPageFetcher fetcher, CampaignDocumentService documents,
            CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            try
            {
                var page = await fetcher.FetchAsync(request.Url, ct);
                var stored = await documents.StoreFetchedPageAsync(id, page, ct);
                db.CampaignDocuments.Add(stored.Document);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/campaigns/{id}/documents/{stored.Document.Id}",
                    new { document = stored.Document.ToDto(), warning = stored.Warning });
            }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = [ex.Message] });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["url"] = ["That page could not be reached. Check the address and your connection."] });
            }
        });

        // The client logo burned into every clip for this campaign. Body is the raw image so a
        // logo upload works the same way a brief upload does.
        campaigns.MapPost("/{id:guid}/branding/logo", async (Guid id, string? fileName, HttpContext context,
            AppDbContext db, CampaignBrandingService branding, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(fileName) || !CampaignBrandingService.IsSupported(fileName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["fileName"] = [$"Unsupported logo format. Accepted formats: {CampaignBrandingService.AcceptedExtensions}."] });
            if (context.Request.ContentLength is > CampaignBrandingService.MaxUploadBytes) return LogoTooLarge();
            AllowRequestBodySize(context, CampaignBrandingService.MaxUploadBytes + 8_192);
            var previous = campaign.ReadBranding();
            try
            {
                var stored = await branding.StoreAsync(id, fileName, context.Request.Body, previous, ct);
                campaign.BrandingJson = JsonSerializer.Serialize(stored, JsonDefaults.Serializer);
                campaign.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                if (previous?.LogoRelativePath is { } replaced && replaced != stored.LogoRelativePath)
                    branding.DeleteFile(replaced);
                return Results.Ok(stored);
            }
            catch (BadHttpRequestException) { return LogoTooLarge(); }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
            }
        });

        campaigns.MapPut("/{id:guid}/branding", async (Guid id, UpdateCampaignBrandingRequest request,
            AppDbContext db, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            var current = campaign.ReadBranding();
            if (current is null) return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["logo"] = ["Upload a logo before configuring how it is placed."] });
            var updated = current with
            {
                Position = request.Position, WidthFraction = request.WidthFraction,
                MarginFraction = request.MarginFraction, Opacity = request.Opacity, Enabled = request.Enabled
            };
            campaign.BrandingJson = JsonSerializer.Serialize(updated, JsonDefaults.Serializer);
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(updated);
        });

        campaigns.MapDelete("/{id:guid}/branding/logo", async (Guid id, AppDbContext db,
            CampaignBrandingService branding, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            var current = campaign.ReadBranding();
            campaign.BrandingJson = null;
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            branding.DeleteFile(current?.LogoRelativePath);
            return Results.NoContent();
        });

        campaigns.MapPut("/{id:guid}/documents/{documentId:guid}", async (Guid id, Guid documentId,
            UpdateCampaignDocumentRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var document = await db.CampaignDocuments.SingleOrDefaultAsync(x => x.Id == documentId && x.CampaignId == id, ct);
            if (document is null) return Results.NotFound();
            document.IncludeInExtraction = request.IncludeInExtraction;
            await db.SaveChangesAsync(ct);
            return Results.Ok(document.ToDto());
        });

        campaigns.MapDelete("/{id:guid}/documents/{documentId:guid}", async (Guid id, Guid documentId,
            AppDbContext db, CampaignDocumentService documents, CancellationToken ct) =>
        {
            var document = await db.CampaignDocuments.SingleOrDefaultAsync(x => x.Id == documentId && x.CampaignId == id, ct);
            if (document is null) return Results.NotFound();
            db.CampaignDocuments.Remove(document);
            await db.SaveChangesAsync(ct);
            documents.DeleteFile(document.RelativePath);
            return Results.NoContent();
        });

        campaigns.MapGet("/{id:guid}/reference-videos", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.ReferenceVideos.AsNoTracking().Where(x => x.CampaignId == id)
                .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.CreatedAt).ToListAsync(ct)).Select(x => x.ToDto())));

        campaigns.MapPost("/{id:guid}/reference-videos", async (Guid id, AddReferenceVideoRequest request,
            IValidator<AddReferenceVideoRequest> validator, AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var url = request.Url.Trim();
            var existing = await db.ReferenceVideos.SingleOrDefaultAsync(x => x.CampaignId == id && x.Url == url, ct);
            if (existing is not null) return Results.Ok(existing.ToDto());
            var reference = new ReferenceVideo
            {
                CampaignId = id, Url = url, Notes = request.Notes,
                Label = DefaultReferenceLabel(request.Label, url),
                IsPrimary = request.IsPrimary || !await db.ReferenceVideos.AnyAsync(x => x.CampaignId == id, ct)
            };
            if (reference.IsPrimary) await ClearPrimaryReferencesAsync(db, id, reference.Id, ct);
            db.ReferenceVideos.Add(reference);
            await db.SaveChangesAsync(ct);
            var job = await coordinator.CreateAsync(id, JobKind.ReferenceAnalysis, new ReferenceJobPayload(reference.Id), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new { reference = reference.ToDto(), job = job.ToDto() });
        });

        // Reference media is streamed to the campaign workspace; only locally derived measurements ever reach a provider.
        campaigns.MapPost("/{id:guid}/reference-videos/upload", async (Guid id, string? fileName, string? label,
            string? notes, bool? isPrimary, HttpContext context, AppDbContext db, WorkspaceService workspace,
            JobCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(fileName) || !IsSupportedReferenceVideo(fileName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["fileName"] = [$"Unsupported video type. Accepted formats: {SupportedReferenceExtensionList}."] });
            if (context.Request.ContentLength is > MaxReferenceUploadBytes) return ReferenceTooLarge();
            AllowRequestBodySize(context, MaxReferenceUploadBytes + 8_192);
            var directory = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "reference"));
            Directory.CreateDirectory(directory);
            var safeName = WorkspaceService.SafeFileName(Path.GetFileName(fileName), "reference");
            var fullPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{safeName}");
            WorkspaceService.EnsureInside(directory, fullPath);
            try
            {
                await using (var file = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 128 * 1024, useAsync: true))
                {
                    await context.Request.Body.CopyToAsync(file, ct);
                }
            }
            catch (BadHttpRequestException)
            {
                TryDeleteFile(fullPath);
                return ReferenceTooLarge();
            }
            var reference = new ReferenceVideo
            {
                CampaignId = id, Notes = notes, Label = DefaultReferenceLabel(label, safeName),
                LocalRelativePath = workspace.ToRelative(fullPath),
                IsPrimary = isPrimary == true || !await db.ReferenceVideos.AnyAsync(x => x.CampaignId == id, ct)
            };
            if (reference.IsPrimary) await ClearPrimaryReferencesAsync(db, id, reference.Id, ct);
            db.ReferenceVideos.Add(reference);
            await db.SaveChangesAsync(ct);
            var job = await coordinator.CreateAsync(id, JobKind.ReferenceAnalysis, new ReferenceJobPayload(reference.Id), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new { reference = reference.ToDto(), job = job.ToDto() });
        });

        campaigns.MapPut("/{id:guid}/reference-videos/{referenceId:guid}", async (Guid id, Guid referenceId,
            UpdateReferenceVideoRequest request, IValidator<UpdateReferenceVideoRequest> validator, AppDbContext db,
            CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            var reference = await db.ReferenceVideos.SingleOrDefaultAsync(x => x.Id == referenceId && x.CampaignId == id, ct);
            if (reference is null) return Results.NotFound();
            reference.Label = DefaultReferenceLabel(request.Label, reference.Url ?? reference.Label);
            reference.Notes = request.Notes;
            reference.IsPrimary = request.IsPrimary;
            if (reference.IsPrimary) await ClearPrimaryReferencesAsync(db, id, reference.Id, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(reference.ToDto());
        });

        campaigns.MapPost("/{id:guid}/reference-videos/{referenceId:guid}/analyze", async (Guid id, Guid referenceId,
            bool? refresh, AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var reference = await db.ReferenceVideos.SingleOrDefaultAsync(x => x.Id == referenceId && x.CampaignId == id, ct);
            if (reference is null) return Results.NotFound();
            reference.Status = ReferenceVideoStatus.Pending;
            reference.Error = null;
            await db.SaveChangesAsync(ct);
            var job = await coordinator.CreateAsync(id, JobKind.ReferenceAnalysis, new ReferenceJobPayload(referenceId, refresh == true), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new { reference = reference.ToDto(), job = job.ToDto() });
        });

        campaigns.MapDelete("/{id:guid}/reference-videos/{referenceId:guid}", async (Guid id, Guid referenceId,
            AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var reference = await db.ReferenceVideos.SingleOrDefaultAsync(x => x.Id == referenceId && x.CampaignId == id, ct);
            if (reference is null) return Results.NotFound();
            var files = new[] { reference.LocalRelativePath, reference.SubtitleRelativePath };
            db.ReferenceVideos.Remove(reference);
            await db.SaveChangesAsync(ct);
            foreach (var relative in files.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                try { TryDeleteFile(workspace.ResolveRelative(relative!)); }
                catch (InvalidOperationException) { }
            }
            return Results.NoContent();
        });

        // Finished clips were only reachable as a text path on the latest render, which is
        // useless once a campaign has several. List them, and stream them back so the UI can
        // play each one in place.
        campaigns.MapGet("/{id:guid}/outputs", async (Guid id, AppDbContext db, WorkspaceService workspace,
            CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var outputRoot = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "output"));
            Directory.CreateDirectory(outputRoot);
            var plans = await db.EditPlans.AsNoTracking().Where(x => x.CampaignId == id).ToListAsync(ct);
            // Loaded once for the whole listing: the caption's required hashtags live on the
            // platform profile, and uploading by hand without them gets a clip rejected.
            var profiles = await db.PlatformProfiles.AsNoTracking().Where(x => x.CampaignId == id).ToListAsync(ct);
            // Each clip says which video it was cut from and when that video came out; the list is newest video first.
            // It also says whether it is a car clip, for the list's "Cars only" switch.
            var sources = (await db.SourceAssets.AsNoTracking().Where(x => x.CampaignId == id)
                    .Select(x => new { x.Id, x.Title, x.Url, x.MetadataJson }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (Title: x.Title ?? x.Url, Day: SourceDates.PublishedOn(x.MetadataJson)));
            RenderedClipDto WithSource(RenderedClipDto clip, EditPlan? plan) =>
                (plan is not null && sources.TryGetValue(plan.SourceAssetId, out var source)
                    ? clip with { SourceTitle = source.Title, SourcePublishedOn = source.Day }
                    : clip) with { Car = ClipTopics.IsCar(plan, clip.Title, clip.Caption) };

            var clips = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
                .Where(x => RenderedClipExtensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                // The music-free preview companion sits beside the final; it is for the in-app player, not a clip.
                .Where(x => !x.EndsWith(".preview.mp4", StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    var platform = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
                    // Renders are named "<plan output name>-<job id>.mp4"; match the plan by prefix.
                    var stem = Path.GetFileNameWithoutExtension(path);
                    var plan = MatchPlanForOutput(plans, stem);
                    var profile = plan is null ? null : profiles.FirstOrDefault(x => x.Platform == plan.Platform);
                    var copy = ClipCaptionComposer.Compose(plan, profile, info.Name);
                    return WithSource(new RenderedClipDto(workspace.ToRelative(path), info.Name, platform, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), plan?.Id, plan?.StartSeconds, plan?.EndSeconds,
                        copy.Title, copy.Caption, PodcastSplitEndpoints.DescribeClip(plan)), plan);
                })
                .ToList();
            // Clips moved to Google Drive: their video is gone from this PC, but the move record beside where it
            // was keeps them in the gallery, shown by their thumbnail, until they are brought back.
            foreach (var record in Directory.EnumerateFiles(outputRoot, "*" + DriveArchive.RecordSuffix, SearchOption.AllDirectories))
            {
                var clipPath = record[..^DriveArchive.RecordSuffix.Length] + ".mp4";
                if (File.Exists(clipPath) || DriveArchive.Read(clipPath) is not { } moved) continue;
                var plan = MatchPlanForOutput(plans, Path.GetFileNameWithoutExtension(clipPath));
                var profile = plan is null ? null : profiles.FirstOrDefault(x => x.Platform == plan.Platform);
                var copy = ClipCaptionComposer.Compose(plan, profile, Path.GetFileName(clipPath));
                clips.Add(WithSource(new RenderedClipDto(workspace.ToRelative(clipPath), Path.GetFileName(clipPath),
                    Path.GetFileName(Path.GetDirectoryName(clipPath)) ?? string.Empty, moved.Bytes, moved.RenderedAt,
                    plan?.Id, plan?.StartSeconds, plan?.EndSeconds, copy.Title, copy.Caption,
                    PodcastSplitEndpoints.DescribeClip(plan), new RenderedClipDriveDto(moved.Remote, moved.MovedAt, moved.Bytes)), plan));
            }
            return Results.Ok(NewestSourceFirst(clips));
        });

        // Streamed with range support so the player can seek without downloading the whole file.
        campaigns.MapGet("/{id:guid}/outputs/file", async (Guid id, string path, AppDbContext db,
            WorkspaceService workspace, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!TryResolveCampaignOutput(workspace, id, path, out var full)) return Results.NotFound();
            var contentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                _ => "application/octet-stream"
            };
            return Results.File(full, contentType, Path.GetFileName(full), enableRangeProcessing: true);
        });

        campaigns.MapPost("/{id:guid}/outputs/reveal", async (Guid id, string path, AppDbContext db,
            WorkspaceService workspace, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!TryResolveCampaignOutput(workspace, id, path, out var full)) return Results.NotFound();
            if (!OperatingSystem.IsWindows())
                return Results.Ok(new { revealed = false, folder = Path.GetDirectoryName(full) });
            try
            {
                using var explorer = new System.Diagnostics.Process();
                explorer.StartInfo.FileName = "explorer.exe";
                explorer.StartInfo.ArgumentList.Add($"/select,{full}");
                explorer.StartInfo.UseShellExecute = false;
                explorer.Start();
                return Results.Ok(new { revealed = true, folder = Path.GetDirectoryName(full) });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                loggerFactory.CreateLogger("RevealOutput").LogWarning(ex, "Could not open the output folder.");
                return Results.Ok(new { revealed = false, folder = Path.GetDirectoryName(full) });
            }
        });

        // Deleting a clip from the gallery sends the clip and its music-free preview to the Recycle Bin,
        // so a wrong click can be undone from Windows. A clip on the YouTube schedule still has to be
        // uploaded from this file, so it must be taken off YouTube first.
        campaigns.MapDelete("/{id:guid}/outputs", async (Guid id, string path, AppDbContext db,
            WorkspaceService workspace, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!TryResolveCampaignOutput(workspace, id, path, out var full)) return Results.NotFound();
            if (await IsOnYouTubeScheduleAsync(db, workspace, id, full, ct))
                return Results.Conflict(new { message = "This clip is on the YouTube schedule. Take it off YouTube first, then delete it." });

            var preview = Path.ChangeExtension(full, null) + ".preview.mp4";
            try
            {
                foreach (var file in new[] { full, preview }.Where(File.Exists))
                    SendToRecycleBin(file, workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "intermediate", "deleted-clips")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                loggerFactory.CreateLogger("DeleteOutput").LogWarning(ex, "Could not delete clip {Path}.", full);
                return Results.Problem("The clip could not be deleted. It may be open in another program.", statusCode: 409);
            }
            return Results.NoContent();
        });

        // Move to Google Drive and back (DriveArchive): one clip per request, so "Move all" in the app can send them
        // one after another and report each. A clip waiting to go up on YouTube still needs its file.
        campaigns.MapPost("/{id:guid}/outputs/drive", async (Guid id, string path, AppDbContext db,
            WorkspaceService workspace, DriveArchive drive, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null || !TryResolveCampaignOutput(workspace, id, path, out var full)) return Results.NotFound();
            if (await IsOnYouTubeScheduleAsync(db, workspace, id, full, ct))
                return Results.Conflict(new { message = "This clip is on the YouTube schedule and its upload needs the file. Move it once it has gone up." });
            try
            {
                var moved = await drive.MoveOutAsync(campaign.Name, full, ct);
                return Results.Ok(new RenderedClipDriveDto(moved.Remote, moved.MovedAt, moved.Bytes));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
        });

        campaigns.MapPost("/{id:guid}/outputs/drive/return", async (Guid id, string path, AppDbContext db,
            WorkspaceService workspace, DriveArchive drive, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!TryResolveMovedOutput(workspace, id, path, out var full)) return Results.NotFound();
            try { await drive.BringBackAsync(full, ct); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
            return Results.NoContent();
        });

        // The thumbnail a moved clip keeps, shown in place of its video.
        campaigns.MapGet("/{id:guid}/outputs/poster", async (Guid id, string path, AppDbContext db,
            WorkspaceService workspace, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!TryResolveMovedOutput(workspace, id, path, out var full)) return Results.NotFound();
            var poster = DriveArchive.PosterPath(full);
            return File.Exists(poster) ? Results.File(poster, "image/jpeg") : Results.NotFound();
        });

        // "Move campaign to Drive": every source video and finished clip of the campaign, as one background job so
        // it carries on when the page is closed. Each file is removed from this PC once it is checked in Drive.
        campaigns.MapGet("/{id:guid}/drive/upload", async (Guid id, AppDbContext db, WorkspaceService workspace,
            DriveArchive drive, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return campaign is null ? Results.NotFound() : Results.Ok(DescribeDriveUpload(workspace, drive, campaign));
        });

        campaigns.MapPost("/{id:guid}/drive/upload", async (Guid id, AppDbContext db, WorkspaceService workspace,
            DriveArchive drive, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            if (await IsDriveJobRunningAsync(db, id, ct))
                return Results.Conflict(new { message = "This campaign is already being moved to or from Google Drive." });
            var found = DescribeDriveUpload(workspace, drive, campaign);
            if (found.Sources + found.Clips == 0)
                return Results.Conflict(new
                {
                    message = found.StillWriting > 0
                        ? "The only videos here are still being written. Try again when the render or download has finished."
                        : "This campaign has no source videos or finished clips left on this PC to move."
                });
            var job = await coordinator.CreateAsync(id, JobKind.DriveUpload, new DriveUploadJobPayload(), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto(campaign.Name));
        });

        // "Bring campaign back from Drive": every clip and source video the campaign moved there, as one background
        // job. The copies in Drive stay.
        campaigns.MapGet("/{id:guid}/drive/return", async (Guid id, AppDbContext db, WorkspaceService workspace,
            DiskSpaceGuard disk, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            return Results.Ok(DescribeDriveReturn(workspace, disk, id));
        });

        campaigns.MapPost("/{id:guid}/drive/return", async (Guid id, AppDbContext db, WorkspaceService workspace,
            DiskSpaceGuard disk, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            if (await IsDriveJobRunningAsync(db, id, ct))
                return Results.Conflict(new { message = "This campaign is already being moved to or from Google Drive." });
            var found = DescribeDriveReturn(workspace, disk, id);
            if (found.Sources + found.Clips == 0)
                return Results.Conflict(new { message = "Nothing of this campaign is in Google Drive; everything is already on this PC." });
            var job = await coordinator.CreateAsync(id, JobKind.DriveReturn, new DriveReturnJobPayload(), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto(campaign.Name));
        });

        campaigns.MapPost("/{id:guid}/exports", async (Guid id, AppDbContext db, WorkspaceService workspace, CancellationToken ct) =>
        {
            var campaign = await DetailQuery(db).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (campaign is null) return Results.NotFound();
            var exportDirectory = workspace.ResolveRelative(Path.Combine("campaigns", id.ToString("N"), "output", "export"));
            Directory.CreateDirectory(exportDirectory);
            var approved = campaign.RequirementRevisions.FirstOrDefault(x => x.IsApproved);
            var markdown = new StringBuilder($"# {campaign.Name}\n\nGenerated {DateTimeOffset.Now:g}\n\n## Publishing profiles\n");
            foreach (var profile in campaign.PlatformProfiles)
                markdown.AppendLine($"- {profile.Platform}: {profile.CaptionTemplate} {string.Join(' ', JsonDefaults.Parse(profile.RequiredHashtagsJson, Array.Empty<string>()))}");
            markdown.AppendLine("\n## Compliance checklist");
            foreach (var item in approved?.Items ?? []) markdown.AppendLine($"- [ ] {item.Value}");
            var summaryPath = Path.Combine(exportDirectory, "publishing-package.md");
            await File.WriteAllTextAsync(summaryPath, markdown.ToString(), ct);
            return Results.Ok(new { folderRelativePath = workspace.ToRelative(exportDirectory), summaryRelativePath = workspace.ToRelative(summaryPath) });
        });
    }

    private static void MapJobs(RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs").WithTags("Jobs");
        jobs.MapGet("/", async (Guid? campaignId, JobStatus? status, int? limit, AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.Jobs.AsNoTracking().AsQueryable();
            if (campaignId.HasValue) query = query.Where(x => x.CampaignId == campaignId);
            if (status.HasValue) query = query.Where(x => x.Status == status);
            // Bounded because the caller polls this every couple of seconds, but the caller says
            // how far back it wants: the list is paged in the UI, and a page count computed over a
            // silently truncated list would report the last page as the last job.
            var take = Math.Clamp(limit ?? 200, 1, 1000);
            var values = await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct);
            var campaignIds = values.Where(x => x.CampaignId.HasValue).Select(x => x.CampaignId!.Value).Distinct().ToArray();
            var names = await db.Campaigns.AsNoTracking().Where(x => campaignIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);

            // Which video each job is working on. Without it every row in a campaign reads the
            // same, and a queue of pipelines cannot be told apart - which is how a run got
            // cancelled and a retry started on the wrong source.
            var sourceIds = values.Select(x => x.PayloadId("sourceAssetId")).OfType<Guid>().Distinct().ToArray();
            var planIds = values.Select(x => x.PayloadId("editPlanId")).OfType<Guid>().Distinct().ToArray();
            var sourceTitles = await db.SourceAssets.AsNoTracking().Where(x => sourceIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Title ?? x.Url, ct);
            // A render is better named by the clip it produces than by the hours-long VOD behind it.
            var planNames = await db.EditPlans.AsNoTracking().Where(x => planIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.OutputFileName, ct);

            string? Subject(JobRecord job)
            {
                if (job.PayloadId("editPlanId") is { } planId && planNames.TryGetValue(planId, out var plan))
                    return Path.GetFileNameWithoutExtension(plan);
                if (job.PayloadId("sourceAssetId") is { } sourceId && sourceTitles.TryGetValue(sourceId, out var title))
                    return title;
                return null;
            }

            return Results.Ok(values.Select(x => x.ToDto(
                x.CampaignId.HasValue && names.TryGetValue(x.CampaignId.Value, out var name) ? name : null,
                Subject(x))));
        });
        jobs.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (job is null) return Results.NotFound();
            var campaignName = job.CampaignId.HasValue ? await db.Campaigns.AsNoTracking().Where(x => x.Id == job.CampaignId.Value).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
            return Results.Ok(job.ToDto(campaignName));
        });
        jobs.MapPost("/{id:guid}/cancel", async (Guid id, JobQueue queue, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (job is null) return Results.NotFound();
            queue.Cancel(id); job.Status = JobStatus.Cancelled; job.Error = "Cancellation requested."; job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); return Results.Ok(job.ToDto());
        });
        jobs.MapPost("/{id:guid}/retry", async (Guid id, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var job = await coordinator.RetryAsync(id, ct);
            return job is null ? Results.Conflict(new { error = "Job cannot be retried." }) : Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });
    }

    private static void MapAi(RouteGroupBuilder api)
    {
        api.MapGet("/ai-analysis/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.AiAnalysisRuns.AsNoTracking().Include(x => x.Highlights).SingleOrDefaultAsync(x => x.Id == id, ct);
            return run is null ? Results.NotFound() : Results.Ok(new AiAnalysisDto(run.Id, run.CampaignId, run.SourceAssetId,
                run.JobId, run.Mode, run.Status, JsonDefaults.Parse(run.ProvidersJson, Array.Empty<string>()), run.Error,
                run.CreatedAt, run.CompletedAt, run.Highlights.OrderBy(x => x.Provider).ThenBy(x => x.Rank).Select(x =>
                    new AiHighlightDto(x.Id, x.Provider, x.Rank, x.StartSeconds, x.EndSeconds, x.Hook, x.Rationale,
                        x.Score, x.Confidence, x.IsSelected, JsonDefaults.ParseElement(x.EditPlanJson) ?? JsonSerializer.SerializeToElement(new { }))).ToList()));
        }).WithTags("AI");
        api.MapPost("/ai-analysis/{id:guid}/cancel", async (Guid id, AppDbContext db, JobQueue queue, CancellationToken ct) =>
        {
            var run = await db.AiAnalysisRuns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null) return Results.NotFound();
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == run.JobId, ct);
            queue.Cancel(run.JobId); run.Status = "Cancelled"; run.CompletedAt = DateTimeOffset.UtcNow;
            if (job is not null && job.Status is JobStatus.Queued or JobStatus.Running)
            {
                job.Status = JobStatus.Cancelled; job.Error = "Cancellation requested."; job.CompletedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct); return Results.Ok(new { run.Id, run.Status, job = job?.ToDto() });
        }).WithTags("AI");
        api.MapPost("/ai-analysis/{id:guid}/select-highlight/{highlightId:guid}", async (Guid id, Guid highlightId, AppDbContext db, CancellationToken ct) =>
        {
            var candidates = await db.HighlightCandidates.Where(x => x.AiAnalysisRunId == id).ToListAsync(ct);
            var selected = candidates.SingleOrDefault(x => x.Id == highlightId); if (selected is null) return Results.NotFound();
            foreach (var candidate in candidates) candidate.IsSelected = candidate.Id == highlightId;
            await db.SaveChangesAsync(ct); return Results.Ok(new { selected.Id, selected.IsSelected });
        }).WithTags("AI");
        api.MapGet("/ai/providers", (IOptions<AppOptions> options) => Results.Ok(new[]
        {
            new { name = "Claude", configuredPath = options.Value.ClaudePath, modes = new[] { "Claude", "Compare" } },
            new { name = "Codex", configuredPath = options.Value.CodexPath, modes = new[] { "Codex", "Auto", "Compare" } }
        })).WithTags("AI");
        api.MapGet("/campaigns/{campaignId:guid}/ai-analysis", async (Guid campaignId, AppDbContext db, CancellationToken ct) =>
        {
            var runs = await db.AiAnalysisRuns.AsNoTracking().Include(x => x.Highlights)
                .Where(x => x.CampaignId == campaignId).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct);
            return Results.Ok(runs.Select(ToAiAnalysisDto));
        }).WithTags("AI");
        api.MapGet("/campaigns/{campaignId:guid}/ai-analysis/latest", async (Guid campaignId, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.AiAnalysisRuns.AsNoTracking().Include(x => x.Highlights)
                .Where(x => x.CampaignId == campaignId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            return run is null ? Results.NotFound() : Results.Ok(ToAiAnalysisDto(run));
        }).WithTags("AI");
    }

    private static void MapEditPlans(RouteGroupBuilder api)
    {
        api.MapGet("/edit-plans/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return plan is null ? Results.NotFound() : Results.Ok(plan.ToDto());
        }).WithTags("Editing");
        api.MapPut("/edit-plans/{id:guid}", async (Guid id, UpdateEditPlanRequest request,
            IValidator<UpdateEditPlanRequest> validator, AppDbContext db, CancellationToken ct) =>
        {
            var invalid = await Validate(request, validator, ct); if (invalid is not null) return invalid;
            var plan = await db.EditPlans.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (plan is null) return Results.NotFound();
            if (plan.IsApproved) return Results.Conflict(new
                { error = "Approved edit plans are immutable. Create a new plan to make changes." });
            plan.StartSeconds = request.StartSeconds; plan.EndSeconds = request.EndSeconds; plan.Width = request.Width; plan.Height = request.Height;
            plan.FramesPerSecond = request.FramesPerSecond; plan.Crf = request.Crf; plan.NormalizeAudio = request.NormalizeAudio;
            plan.BurnSubtitles = request.BurnSubtitles; plan.SubtitleRelativePath = request.SubtitleRelativePath;
            plan.MusicAssetId = request.MusicAssetId; plan.MusicVolume = request.MusicVolume;
            plan.OutputFileName = request.OutputFileName; plan.IsApproved = request.IsApproved;
            plan.ExtraSettingsJson = JsonSerializer.Serialize(new EditPlanExtrasDto(request.Framing ?? "CenterCrop", request.CtaOverlayText,
                request.SubtitleStyle, request.MusicDuckingDb ?? -12, request.SoundEffects ?? [], request.Overlays ?? [],
                request.ColorFlashes, request.ZoomPunches), JsonDefaults.Serializer);
            plan.LastFfmpegArgumentsJson = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(plan.ToDto());
        }).WithTags("Editing");
        api.MapPost("/edit-plans/{id:guid}/approve", async (Guid id, ApproveEditPlanRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (plan is null) return Results.NotFound();
            if (plan.IsApproved && !request.Approved)
                return Results.Conflict(new { error = "Approved edit plans are immutable and cannot be unapproved." });
            plan.IsApproved = request.Approved; await db.SaveChangesAsync(ct); return Results.Ok(plan.ToDto());
        }).WithTags("Editing");
        // Gives an existing plan the campaign's house style - the sound cues, colour washes and
        // punch-ins every other clip has - without moving its in and out points. A plan built by
        // hand, from a transcript read rather than from the window heuristic, otherwise renders
        // bare: correct content, none of the styling. Approved plans are immutable, so this
        // produces a new plan rather than editing the old one.
        api.MapPost("/edit-plans/{id:guid}/restyle", async (Guid id, AppDbContext db, WorkspaceService workspace,
            ClipStylist clipStylist, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (plan is null) return Results.NotFound();
            var source = await db.SourceAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == plan.SourceAssetId, ct);
            if (source is null || string.IsNullOrWhiteSpace(source.LocalRelativePath))
                return Results.Conflict(new { error = "Restyling reads the source media, which is no longer on disk for this plan." });
            if (string.IsNullOrWhiteSpace(source.WordTranscriptRelativePath))
                return Results.Conflict(new { error = "Restyling needs the source's word transcript. Run a transcribe job first." });

            var transcript = CaptionBuilder.ReadTranscript(await File.ReadAllTextAsync(
                workspace.ResolveRelative(source.WordTranscriptRelativePath!), ct));
            var assets = await db.MediaLibraryAssets.AsNoTracking().ToListAsync(ct);
            var rotation = await db.EditPlans.CountAsync(x => x.CampaignId == plan.CampaignId, ct);
            var style = await clipStylist.BuildForWindowAsync(workspace.ResolveRelative(source.LocalRelativePath!),
                plan.StartSeconds, plan.EndSeconds, transcript.Words, assets, rotation, ct);

            var extras = JsonSerializer.Deserialize<EditPlanExtrasDto>(plan.ExtraSettingsJson, JsonDefaults.Serializer);
            var restyled = new EditPlan
            {
                CampaignId = plan.CampaignId, SourceAssetId = plan.SourceAssetId, Platform = plan.Platform,
                StartSeconds = plan.StartSeconds, EndSeconds = plan.EndSeconds,
                Width = plan.Width, Height = plan.Height, FramesPerSecond = plan.FramesPerSecond,
                Crf = plan.Crf, NormalizeAudio = plan.NormalizeAudio, BurnSubtitles = plan.BurnSubtitles,
                SubtitleRelativePath = plan.SubtitleRelativePath,
                MusicAssetId = style.MusicAssetId ?? plan.MusicAssetId,
                MusicVolume = style.MusicAssetId is null ? plan.MusicVolume : style.MusicVolume,
                OutputFileName = plan.OutputFileName, IsApproved = true,
                ExtraSettingsJson = JsonSerializer.Serialize(new EditPlanExtrasDto(
                    extras?.Framing ?? "CenterCrop", extras?.CtaOverlayText, extras?.SubtitleStyle,
                    extras?.MusicDuckingDb ?? -12, style.SoundEffects, extras?.Overlays ?? [],
                    style.ColorFlashes, style.ZoomPunches), JsonDefaults.Serializer)
            };
            db.EditPlans.Add(restyled);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/edit-plans/{restyled.Id}", restyled.ToDto());
        }).WithTags("Editing");

        api.MapPost("/edit-plans/{id:guid}/renders", async (Guid id, AppDbContext db, JobCoordinator coordinator,
            IOptions<AppOptions> options, CancellationToken ct) =>
        {
            var plan = await db.EditPlans.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (plan is null) return Results.NotFound();
            if (options.Value.RequireEditApproval && !plan.IsApproved)
                return Results.Conflict(new { error = "The edit plan must be approved before rendering.", editPlanId = plan.Id });
            if (!plan.IsApproved)
            {
                plan.IsApproved = true;
                await db.SaveChangesAsync(ct);
            }
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.Render, new RenderJobPayload(plan.Id), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        }).WithTags("Editing");
        api.MapPost("/renders/{id:guid}/cancel", async (Guid id, JobQueue queue, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == id && x.Kind == JobKind.Render, ct);
            if (job is null) return Results.NotFound(); queue.Cancel(id); job.Status = JobStatus.Cancelled; await db.SaveChangesAsync(ct); return Results.Ok(job.ToDto());
        }).WithTags("Editing");
        api.MapPost("/renders/{id:guid}/validate", async (Guid id, AppDbContext db, JobCoordinator coordinator, CancellationToken ct) =>
        {
            var render = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Kind == JobKind.Render, ct);
            if (render is null || render.Status != JobStatus.Completed || string.IsNullOrWhiteSpace(render.ResultJson)) return Results.Conflict(new { error = "Render is not complete." });
            using var result = JsonDocument.Parse(render.ResultJson);
            var relative = result.RootElement.GetProperty("outputRelativePath").GetString()!;
            var planId = result.RootElement.GetProperty("editPlanId").GetGuid();
            var plan = await db.EditPlans.AsNoTracking().SingleAsync(x => x.Id == planId, ct);
            var profile = await db.PlatformProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.CampaignId == plan.CampaignId && x.Platform == plan.Platform, ct);
            var job = await coordinator.CreateAsync(plan.CampaignId, JobKind.Validate,
                new ValidateJobPayload(relative, profile?.MinDurationSeconds ?? 0, profile?.MaxDurationSeconds ?? 3600, plan.Width, plan.Height), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        }).WithTags("Editing");
    }

    private static void MapMediaLibrary(RouteGroupBuilder api)
    {
        var media = api.MapGroup("/media-library").WithTags("Media library");
        media.MapGet("/locations", async (AppDbContext db, CancellationToken ct) => Results.Ok((await db.MediaLibraryLocations.AsNoTracking()
            .OrderBy(x => x.Path).ToListAsync(ct)).Select(x => new MediaLocationDto(x.Id, x.Path, x.Kind, x.Enabled, x.CreatedAt))));
        media.MapPost("/locations", async (AddMediaLocationRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var fullPath = Path.GetFullPath(request.Path);
            if (!Directory.Exists(fullPath)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["path"] = ["Directory does not exist."] });
            var location = new MediaLibraryLocation { Path = fullPath, Kind = request.Kind };
            db.MediaLibraryLocations.Add(location); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/media-library/locations/{location.Id}", new MediaLocationDto(location.Id, location.Path, location.Kind, location.Enabled, location.CreatedAt));
        });
        media.MapPost("/scan", async (Guid? locationId, MediaLibraryService service, CancellationToken ct) => Results.Ok(new { indexed = await service.ScanAsync(locationId, ct) }));
        media.MapGet("/assets", async (string? search, MediaAssetKind? kind, bool? favorite, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.MediaLibraryAssets.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.FileName.ToLower().Contains(search.ToLower()));
            if (kind.HasValue) query = query.Where(x => x.Kind == kind); if (favorite.HasValue) query = query.Where(x => x.IsFavorite == favorite);
            return Results.Ok((await query.OrderBy(x => x.FileName).ToListAsync(ct)).Select(x => x.ToDto()));
        });
        // Streams a music/sfx file with range support, so the app's live preview can play a song under
        // the sliders. Serves only a file that is still inside its indexed location folder.
        media.MapGet("/assets/{id:guid}/file", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var asset = await db.MediaLibraryAssets.AsNoTracking().Include(x => x.Location)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (asset is null) return Results.NotFound();
            var full = Path.GetFullPath(asset.FullPath);
            if (!full.StartsWith(Path.GetFullPath(asset.Location.Path), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Results.NotFound();
            var contentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".mp3" => "audio/mpeg", ".m4a" or ".aac" => "audio/mp4", ".wav" => "audio/wav",
                ".ogg" => "audio/ogg", ".flac" => "audio/flac", _ => "application/octet-stream"
            };
            return Results.File(full, contentType, Path.GetFileName(full), enableRangeProcessing: true);
        });
        media.MapPut("/assets/{id:guid}", async (Guid id, UpdateMediaAssetRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var asset = await db.MediaLibraryAssets.SingleOrDefaultAsync(x => x.Id == id, ct); if (asset is null) return Results.NotFound();
            asset.TagsJson = JsonDefaults.StringArray(request.Tags ?? []); asset.Mood = request.Mood; asset.Energy = request.Energy; asset.Genre = request.Genre;
            asset.Bpm = request.Bpm; asset.IsFavorite = request.Favorite; asset.LicenseNotes = request.LicenseNotes;
            await db.SaveChangesAsync(ct); return Results.Ok(asset.ToDto());
        });
        api.MapGet("/audio-assets", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.MediaLibraryAssets.AsNoTracking().OrderBy(x => x.FileName).ToListAsync(ct)).Select(x => x.ToDto()))).WithTags("Media library");
    }

    private static void MapSettings(RouteGroupBuilder api)
    {
        api.MapGet("/settings", (IOptions<AppOptions> options, SettingsRuntimeState state) => Results.Ok(new SettingsDto(options.Value.WorkspaceRoot,
            options.Value.WorkspaceRoot, options.Value.YtDlpPath, options.Value.FfmpegPath, options.Value.FfprobePath,
            options.Value.ClaudePath, options.Value.CodexPath, options.Value.ProcessTimeoutMinutes, options.Value.ProcessTimeoutMinutes,
            options.Value.DefaultAiMode, options.Value.MaxConcurrentMediaJobs, options.Value.RequireEditApproval,
            options.Value.PreferredVideoQuality, state.RestartRequired, options.Value.OverlayFontPath,
            options.Value.CookiesFromBrowser, options.Value.CookiesFilePath, options.Value.JsRuntime))).WithTags("Settings");
        api.MapPut("/settings", async (UpdateSettingsRequest request, SettingsRuntimeState state, CancellationToken ct) =>
        {
            if (request.ProcessTimeoutMinutes is < 1 or > 240 || request.MaxConcurrentMediaJobs is < 1 or > 8 ||
                request.PreferredVideoQuality is not ("Best" or "1080p" or "720p") ||
                string.IsNullOrWhiteSpace(request.WorkspaceRoot) ||
                !new[] { request.YtDlpPath, request.FfmpegPath, request.FfprobePath, request.ClaudePath, request.CodexPath }
                    .All(IsSafeExecutableSetting))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Timeout, concurrency, or preferred quality is invalid."] });
            if (!string.IsNullOrWhiteSpace(request.JsRuntime) && !YtDlpCookieSources.IsSupportedJsRuntime(request.JsRuntime))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["jsRuntime"] = [$"Choose one of: {string.Join(", ", YtDlpCookieSources.JsRuntimes)}."] });
            if (!string.IsNullOrWhiteSpace(request.CookiesFromBrowser) && !YtDlpCookieSources.IsSupportedBrowser(request.CookiesFromBrowser))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["cookiesFromBrowser"] = [$"Choose one of: {string.Join(", ", YtDlpCookieSources.Browsers)}."] });
            try
            {
                _ = Path.GetFullPath(request.WorkspaceRoot);
                if (!string.IsNullOrWhiteSpace(request.OverlayFontPath) && !File.Exists(Path.GetFullPath(request.OverlayFontPath)))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["overlayFontPath"] = ["Overlay font file does not exist."] });
                if (!string.IsNullOrWhiteSpace(request.CookiesFilePath) && !File.Exists(Path.GetFullPath(request.CookiesFilePath)))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["cookiesFilePath"] = ["Cookies file does not exist."] });
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["workspaceRoot"] = ["Workspace path is invalid."] });
            }
            var localSettings = JsonSerializer.Serialize(new { WhopClipper = new
            {
                request.WorkspaceRoot, request.YtDlpPath, request.FfmpegPath, request.FfprobePath,
                request.ClaudePath, request.CodexPath, request.ProcessTimeoutMinutes, request.DefaultAiMode,
                request.MaxConcurrentMediaJobs, request.RequireEditApproval, request.PreferredVideoQuality,
                request.OverlayFontPath, request.CookiesFromBrowser, request.CookiesFilePath, request.JsRuntime
            } }, new JsonSerializerOptions(JsonDefaults.Serializer) { WriteIndented = true });
            await LocalSettingsFile.WriteAtomicallyAsync(localSettings, ct);
            state.MarkRestartRequired();
            return Results.Ok(new { saved = true, restartRequiredToApplyChanges = true, settingsPath = LocalSettingsFile.Path });
        }).WithTags("Settings");
        // Media left behind when a campaign's rows were deleted but its folder could not be. The
        // app has nothing pointing at it afterwards, so without this the disk it holds never
        // comes back and nobody can tell why.
        api.MapGet("/system/orphaned-campaign-folders", async (WorkspaceMaintenanceService maintenance,
            CancellationToken ct) => Results.Ok(await maintenance.FindOrphansAsync(ct))).WithTags("System");

        api.MapDelete("/system/orphaned-campaign-folders/{folderName}", async (string folderName,
            WorkspaceMaintenanceService maintenance, CancellationToken ct) =>
        {
            try
            {
                return await maintenance.DeleteOrphanAsync(folderName, ct)
                    ? Results.Ok(new { deleted = true })
                    : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["folderName"] = [ex.Message] });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["folderName"] = ["The folder is still in use. Close anything reading from it and try again."],
                });
            }
        }).WithTags("System");

        api.MapGet("/system/dependencies", async (DependencyService service, CancellationToken ct) => Results.Ok(await service.CheckAsync(ct))).WithTags("System");
        api.MapGet("/tool-status", async (DependencyService service, CancellationToken ct) => Results.Ok(await service.CheckAsync(ct))).WithTags("System");
    }

    private static IQueryable<Campaign> DetailQuery(AppDbContext db) => db.Campaigns
        .Include(x => x.RequirementRevisions).ThenInclude(x => x.Items)
        .Include(x => x.PlatformProfiles).Include(x => x.Sources)
        .Include(x => x.Documents).Include(x => x.ReferenceVideos).AsSplitQuery();

    private static AiAnalysisDto ToAiAnalysisDto(AiAnalysisRun run) => new(run.Id, run.CampaignId, run.SourceAssetId,
        run.JobId, run.Mode, run.Status, JsonDefaults.Parse(run.ProvidersJson, Array.Empty<string>()), run.Error,
        run.CreatedAt, run.CompletedAt, run.Highlights.OrderBy(x => x.Provider).ThenBy(x => x.Rank).Select(x =>
            new AiHighlightDto(x.Id, x.Provider, x.Rank, x.StartSeconds, x.EndSeconds, x.Hook, x.Rationale,
                x.Score, x.Confidence, x.IsSelected, JsonDefaults.ParseElement(x.EditPlanJson) ??
                JsonSerializer.SerializeToElement(new { }))).ToList());

    private static PlatformProfile CloneProfile(PlatformProfile source) => new()
    {
        Platform = source.Platform, Enabled = source.Enabled, Width = source.Width, Height = source.Height,
        MinDurationSeconds = source.MinDurationSeconds, PreferredDurationSeconds = source.PreferredDurationSeconds,
        MaxDurationSeconds = source.MaxDurationSeconds, MaxFileSizeBytes = source.MaxFileSizeBytes,
        CaptionTemplate = source.CaptionTemplate, RequiredHashtagsJson = source.RequiredHashtagsJson,
        RequiredMentionsJson = source.RequiredMentionsJson, DisclosureText = source.DisclosureText,
        CallToAction = source.CallToAction, BurnSubtitles = source.BurnSubtitles, SafeAreaNotes = source.SafeAreaNotes,
        ComplianceChecklistJson = source.ComplianceChecklistJson, ValueOriginsJson = source.ValueOriginsJson
    };

    private static async Task<IResult?> Validate<T>(T value, IValidator<T> validator, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(value, ct);
        return result.IsValid ? null : Results.ValidationProblem(result.Errors.GroupBy(x => x.PropertyName)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ErrorMessage).ToArray()));
    }

    private const long MaxReferenceUploadBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] SupportedReferenceExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv"];
    private static string SupportedReferenceExtensionList => string.Join(", ", SupportedReferenceExtensions);

    private static bool IsSupportedReferenceVideo(string fileName) =>
        SupportedReferenceExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Raises Kestrel's per-request body cap so large uploads are bounded here instead of by the global default.</summary>
    private static void AllowRequestBodySize(HttpContext context, long maxBytes)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly) feature.MaxRequestBodySize = maxBytes;
    }

    private static IResult DocumentTooLarge() => Results.Json(new
    {
        error = $"The brief is larger than the {CampaignDocumentService.MaxUploadBytes / (1024 * 1024)} MB upload limit."
    }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult LogoTooLarge() => Results.Json(new
    {
        error = $"The logo is larger than the {CampaignBrandingService.MaxUploadBytes / (1024 * 1024)} MB upload limit."
    }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult ReferenceTooLarge() => Results.Json(new
    {
        error = $"The reference video is larger than the {MaxReferenceUploadBytes / (1024 * 1024 * 1024)} GB upload limit."
    }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static string DefaultReferenceLabel(string? requested, string fallbackSource)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim()[..Math.Min(requested.Trim().Length, 200)];
        if (Uri.TryCreate(fallbackSource, UriKind.Absolute, out var uri)) return $"{uri.Host}{uri.AbsolutePath}"[..Math.Min($"{uri.Host}{uri.AbsolutePath}".Length, 200)];
        return string.IsNullOrWhiteSpace(fallbackSource) ? "Reference" : fallbackSource[..Math.Min(fallbackSource.Length, 200)];
    }

    private static async Task ClearPrimaryReferencesAsync(AppDbContext db, Guid campaignId, Guid keepId, CancellationToken ct)
    {
        foreach (var other in await db.ReferenceVideos.Where(x => x.CampaignId == campaignId && x.IsPrimary && x.Id != keepId).ToListAsync(ct))
            other.IsPrimary = false;
    }

    private static async Task CopyDocumentAsync(AppDbContext db, WorkspaceService workspace, Guid campaignId,
        CampaignDocument original, CancellationToken ct)
    {
        var sourcePath = workspace.ResolveRelative(original.RelativePath);
        if (!File.Exists(sourcePath)) return;
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "campaign", "documents"));
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{original.FileName}");
        WorkspaceService.EnsureInside(directory, targetPath);
        await using (var input = File.OpenRead(sourcePath))
        await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, ct);
        }
        db.CampaignDocuments.Add(new CampaignDocument
        {
            CampaignId = campaignId, FileName = original.FileName, RelativePath = workspace.ToRelative(targetPath),
            Kind = original.Kind, SizeBytes = original.SizeBytes, ContentHash = original.ContentHash,
            ExtractedText = original.ExtractedText, ExtractedCharacterCount = original.ExtractedCharacterCount,
            PageCount = original.PageCount, ExtractionStatus = original.ExtractionStatus,
            ExtractionError = original.ExtractionError, IncludeInExtraction = original.IncludeInExtraction
        });
    }

    private static void TryDeleteFile(string fullPath)
    {
        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] RenderedClipExtensions = [".mp4", ".m4v", ".webm", ".mov"];

    /// <summary>
    /// Clips from the newest source video first, and within one video the newest render first. Clips whose source
    /// date is not known yet come after, newest render first, as the whole list used to be.
    /// </summary>
    internal static List<RenderedClipDto> NewestSourceFirst(IEnumerable<RenderedClipDto> clips) => clips
        .OrderByDescending(x => x.SourcePublishedOn.HasValue).ThenByDescending(x => x.SourcePublishedOn)
        .ThenByDescending(x => x.CreatedAt).ToList();

    /// <summary>A move to or from Drive for this campaign is waiting or running: two at once would trip over each other.</summary>
    private static Task<bool> IsDriveJobRunningAsync(AppDbContext db, Guid campaignId, CancellationToken ct) =>
        db.Jobs.AnyAsync(x => x.CampaignId == campaignId && (x.Kind == JobKind.DriveUpload || x.Kind == JobKind.DriveReturn) &&
                              (x.Status == JobStatus.Queued || x.Status == JobStatus.Running), ct);

    private static CampaignDriveReturnDto DescribeDriveReturn(WorkspaceService workspace, DiskSpaceGuard disk, Guid campaignId)
    {
        var files = CampaignDriveReturn.Find(workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"))));
        var sources = files.Where(x => x.Group == "source").ToList();
        var clips = files.Where(x => x.Group == "clip").ToList();
        return new CampaignDriveReturnDto(sources.Count, sources.Sum(x => x.Bytes), clips.Count, clips.Sum(x => x.Bytes), disk.Available);
    }

    private static CampaignDriveUploadDto DescribeDriveUpload(WorkspaceService workspace, DriveArchive drive, Campaign campaign)
    {
        var remote = drive.RemoteFor(campaign.Name);
        var found = CampaignDriveFiles.Find(workspace.ResolveRelative(Path.Combine("campaigns", campaign.Id.ToString("N"))),
            remote, DateTime.UtcNow);
        var sources = found.Files.Where(x => x.Group == "source").ToList();
        var clips = found.Files.Where(x => x.Group == "clip").ToList();
        return new CampaignDriveUploadDto(remote, sources.Count, sources.Sum(x => x.Bytes), clips.Count, clips.Sum(x => x.Bytes),
            found.AlreadyMoved, found.StillWriting);
    }

    /// <summary>Publish states that still hold a YouTube slot for a clip or need its file to upload.</summary>
    private static readonly PublishStatus[] ScheduledOrUploading =
        [PublishStatus.Queued, PublishStatus.Validating, PublishStatus.Uploading, PublishStatus.Processing,
         PublishStatus.Scheduled, PublishStatus.NeedsAttention];

    /// <summary>True while a YouTube upload still needs this clip's file.</summary>
    private static async Task<bool> IsOnYouTubeScheduleAsync(AppDbContext db, WorkspaceService workspace, Guid campaignId,
        string fullPath, CancellationToken ct)
    {
        var normalized = workspace.ToRelative(fullPath).Replace('\\', '/');
        var scheduled = await db.PublishJobs.AsNoTracking()
            .Where(x => x.CampaignId == campaignId && ScheduledOrUploading.Contains(x.Status))
            .Select(x => x.OutputRelativePath)
            .ToListAsync(ct);
        return scheduled.Any(x => string.Equals(x.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Like <see cref="TryResolveCampaignOutput"/> for a clip whose video was moved to Google Drive: the
    /// clip file is gone, and its move record must be there instead.</summary>
    private static bool TryResolveMovedOutput(WorkspaceService workspace, Guid campaignId, string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            var outputRoot = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "output"));
            var candidate = workspace.ResolveRelative(relativePath);
            WorkspaceService.EnsureInside(outputRoot, candidate);
            if (!RenderedClipExtensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase)) return false;
            if (!File.Exists(DriveArchive.RecordPath(candidate))) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The Windows Recycle Bin, so a deleted clip can be restored. Anywhere the bin is not
    /// available the file moves to <paramref name="fallbackFolder"/> instead of being erased.</summary>
    private static void SendToRecycleBin(string fullPath, string fallbackFolder)
    {
        if (OperatingSystem.IsWindows() && RecycleBin.TrySend(fullPath)) return;
        Directory.CreateDirectory(fallbackFolder);
        File.Move(fullPath, Path.Combine(fallbackFolder, Path.GetFileName(fullPath)), overwrite: true);
    }

    /// <summary>Resolves a client-supplied path and confirms it names a real file inside this
    /// campaign's output folder, so no other part of the workspace can be read through it.</summary>
    private static bool TryResolveCampaignOutput(WorkspaceService workspace, Guid campaignId, string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            var outputRoot = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "output"));
            var candidate = workspace.ResolveRelative(relativePath);
            WorkspaceService.EnsureInside(outputRoot, candidate);
            if (!File.Exists(candidate)) return false;
            if (!RenderedClipExtensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeExecutableSetting(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2_000 &&
        !value.Any(char.IsControl) && !value.Contains('"');

}
