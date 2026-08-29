using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed record PublishingMediaValidation(bool Valid, string? FullPath, string? FileName,
    long? SizeBytes, string? Sha256, double? DurationSeconds, string? Error);

public sealed class PublishingMediaValidator(AppDbContext db, WorkspaceService workspace, IProcessRunner runner,
    IOptions<AppOptions> options)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm" };

    public async Task<PublishingMediaValidation> ValidateAsync(Guid campaignId, string relativePath,
        PlatformKind platform, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return Invalid("Choose a rendered campaign output.");
        string fullPath;
        try
        {
            var outputRoot = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "output"));
            fullPath = workspace.ResolveRelative(relativePath);
            WorkspaceService.EnsureInside(outputRoot, fullPath);
            if (HasReparsePoint(outputRoot, fullPath)) return Invalid("Output path cannot pass through a junction or symbolic link.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        { return Invalid("Output path is outside this campaign's output folder."); }
        if (!File.Exists(fullPath)) return Invalid("Rendered output file does not exist.");
        if (!Extensions.Contains(Path.GetExtension(fullPath))) return Invalid("Only rendered MP4, M4V, MOV, or WebM videos can be published.");
        var info = new FileInfo(fullPath);
        if (info.Length == 0) return Invalid("Rendered output file is empty.");
        var maximum = platform == PlatformKind.InstagramReels ? 1L * 1024 * 1024 * 1024 : 256L * 1024 * 1024 * 1024;
        if (info.Length > maximum) return Invalid($"Video exceeds the {maximum / (1024 * 1024)} MB platform safety limit.");

        var result = await runner.RunAsync(new ProcessInvocation(options.Value.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", fullPath],
            Path.GetDirectoryName(fullPath)!, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        if (result.TimedOut || result.ExitCode != 0) return Invalid("FFprobe could not validate the rendered video.");
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var streams = json.RootElement.GetProperty("streams");
            var videoStream = streams.EnumerateArray().FirstOrDefault(x =>
                x.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
            if (videoStream.ValueKind == JsonValueKind.Undefined)
                return Invalid("The selected file has no video stream.");
            var durationRaw = json.RootElement.GetProperty("format").GetProperty("duration").GetString();
            var duration = double.TryParse(durationRaw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            if (duration <= 0) return Invalid("The selected video has an invalid duration.");
            var profile = await db.PlatformProfiles.AsNoTracking().SingleOrDefaultAsync(
                x => x.CampaignId == campaignId && x.Platform == platform, cancellationToken);
            if (profile is not null && !profile.Enabled) return Invalid("This platform is disabled in the campaign requirements.");
            if (profile is not null && (duration < profile.MinDurationSeconds - 0.1 || duration > profile.MaxDurationSeconds + 0.1))
                return Invalid($"Video duration must be between {profile.MinDurationSeconds:0.#} and {profile.MaxDurationSeconds:0.#} seconds for this campaign.");
            if (profile?.MaxFileSizeBytes is long profileMax && info.Length > profileMax)
                return Invalid("Video exceeds the campaign's platform file-size requirement.");
            if (profile is not null && profile.Width > 0 && profile.Height > 0 &&
                videoStream.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var width) &&
                videoStream.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var height) &&
                (width != profile.Width || height != profile.Height))
                return Invalid($"Video resolution is {width}x{height}; this campaign requires {profile.Width}x{profile.Height} for the selected platform.");
            await using var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
            return new(true, fullPath, info.Name, info.Length, sha256, duration, null);
        }
        catch (JsonException) { return Invalid("FFprobe returned unreadable media metadata."); }
        catch (InvalidOperationException) { return Invalid("FFprobe returned incomplete media metadata."); }
        catch (IOException) { return Invalid("The rendered video changed or became unreadable during validation."); }
        catch (UnauthorizedAccessException) { return Invalid("The rendered video cannot be read with the current Windows user."); }
    }

    private static PublishingMediaValidation Invalid(string error) => new(false, null, null, null, null, null, error);

    internal static bool HasReparsePoint(string root, string target)
    {
        var rootFull = Path.GetFullPath(root);
        var targetFull = Path.GetFullPath(target);
        WorkspaceService.EnsureInside(rootFull, targetFull);
        var relative = Path.GetRelativePath(rootFull, targetFull);
        var current = rootFull;
        if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }
}

public sealed record PublishJobCreationResult(PublishJob? Job, Dictionary<string, string[]>? Errors,
    bool Duplicate = false, bool Conflict = false, bool CampaignNotFound = false);

public sealed class PublishJobService(AppDbContext db, PublishingMediaValidator mediaValidator,
    PlatformConnectionService connections, PlatformAccountKitService accountKits)
{
    public async Task<PublishJobCreationResult> CreateAsync(Guid campaignId,
        CreatePublishJobRequest request, CancellationToken cancellationToken)
    {
        var errors = ValidateRequest(request);
        if (errors.Count > 0) return new(null, errors);
        if (!await db.Campaigns.AsNoTracking().AnyAsync(x => x.Id == campaignId, cancellationToken))
            return new(null, null, CampaignNotFound: true);
        var validation = await mediaValidator.ValidateAsync(campaignId, request.OutputRelativePath, request.Platform, cancellationToken);
        if (!validation.Valid) return new(null, new() { ["outputRelativePath"] = [validation.Error!] });
        var copyErrors = await ValidatePublishingCopyAsync(campaignId, request.Platform, request.Caption, cancellationToken);
        if (copyErrors.Count > 0) return new(null, copyErrors);

        string accountIdentifier;
        if (request.Platform == PlatformKind.TikTok)
        {
            var kit = await accountKits.GetAsync(campaignId, request.Platform, cancellationToken);
            var handle = kit?.Fields.FirstOrDefault(x => x.Key.Equals("handle", StringComparison.OrdinalIgnoreCase))?.Value;
            accountIdentifier = string.IsNullOrWhiteSpace(handle) ? "Confirm the TikTok account in the upload page" : $"@{handle.TrimStart('@')}";
        }
        else if (request.DryRun) accountIdentifier = "mock/dry-run destination";
        else
        {
            var connection = await connections.GetAsync(request.Platform, cancellationToken);
            if (!connection.CanPublish) return new(null, new() { ["platform"] = [connection.CapabilityMessage] });
            if (string.IsNullOrWhiteSpace(connection.AccountIdentifier))
                return new(null, new() { ["platform"] = ["Validate and identify the exact destination account before publishing."] });
            accountIdentifier = connection.AccountIdentifier;
        }

        var idempotencyKey = request.IdempotencyKey!.Trim();
        var normalizedPath = request.OutputRelativePath.Trim().Replace('\\', '/');
        var requestHash = ComputeRequestHash(campaignId, request.Platform, normalizedPath, request.Title.Trim(),
            request.Caption.Trim(), request.Privacy.ToLowerInvariant(), request.ShareToFeed, request.DryRun,
            validation.Sha256!, accountIdentifier);
        var duplicate = await db.PublishJobs.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (duplicate is not null)
            return string.Equals(duplicate.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(duplicate, null, Duplicate: true)
                : new(null, new() { ["idempotencyKey"] = ["This idempotency key is already bound to different video, metadata, account, or destination settings."] }, Conflict: true);
        var recentSameOutput = await db.PublishJobs.AsNoTracking().AnyAsync(x => x.CampaignId == campaignId &&
            x.Platform == request.Platform && x.FileSha256 == validation.Sha256 &&
            x.Status != PublishStatus.Failed && x.Status != PublishStatus.Cancelled &&
            x.CreatedAt > DateTimeOffset.UtcNow.AddHours(-24), cancellationToken);
        if (recentSameOutput) return new(null, new() { ["outputRelativePath"] = ["This exact video already has a recent publish attempt for that platform. Use its existing status instead of risking a duplicate post."] }, Conflict: true);
        var job = new PublishJob
        {
            CampaignId = campaignId, Platform = request.Platform, OutputRelativePath = normalizedPath,
            FileName = validation.FileName!, Title = request.Title.Trim(), Caption = request.Caption.Trim(),
            Privacy = request.Privacy.ToLowerInvariant(), ShareToFeed = request.ShareToFeed,
            DryRun = request.DryRun, IdempotencyKey = idempotencyKey, RequestHash = requestHash,
            FileSha256 = validation.Sha256!, FileSizeBytes = validation.SizeBytes!.Value,
            AccountIdentifier = accountIdentifier
        };
        db.PublishJobs.Add(job);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.Entry(job).State = EntityState.Detached;
            duplicate = await db.PublishJobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (duplicate is null) throw;
            return string.Equals(duplicate.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(duplicate, null, Duplicate: true)
                : new(null, new() { ["idempotencyKey"] = ["This idempotency key is already bound to a different publishing request."] }, Conflict: true);
        }
        return new(job, null);
    }

    public async Task<PublishJob?> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.PublishJobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return null;
        if (job.Status is not (PublishStatus.Queued or PublishStatus.AwaitingFinalConfirmation or PublishStatus.RequiresManualAction)) return job;
        job.Status = PublishStatus.Cancelled; job.UpdatedAt = DateTimeOffset.UtcNow; job.Error = "Cancelled before upload started.";
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    internal static Dictionary<string, string[]> ValidateRequest(CreatePublishJobRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.Confirmed) errors["confirmed"] = ["Review the destination, video and metadata, then explicitly confirm this publish attempt."];
        if (!PlatformPublishingCatalog.IsPublishingPlatform(request.Platform)) errors["platform"] = ["Unsupported publishing platform."];
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 || request.IdempotencyKey.Any(char.IsControl))
            errors["idempotencyKey"] = ["Provide a stable idempotency key (crypto.randomUUID is recommended)."];
        if (string.IsNullOrWhiteSpace(request.OutputRelativePath)) errors["outputRelativePath"] = ["Choose a rendered campaign output."];
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 100) errors["title"] = ["Title is required and must be 100 characters or fewer."];
        if (request.Caption is null || request.Caption.Length > 5_000) errors["caption"] = ["Caption must be 5,000 characters or fewer."];
        var privacy = request.Privacy?.ToLowerInvariant();
        if (privacy is not ("private" or "unlisted" or "public")) errors["privacy"] = ["Privacy must be private, unlisted, or public."];
        if (request.Platform == PlatformKind.InstagramReels && privacy != "public") errors["privacy"] = ["Instagram Reels do not support YouTube-style privacy; choose public after reviewing the post."];
        return errors;
    }

    public async Task<(PublishJob? Job, Dictionary<string, string[]>? Errors, bool Conflict)> ConfirmInstagramAsync(
        Guid id, ConfirmInstagramPublishRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.Confirmed) errors["confirmed"] = ["Review the processed Reel container and exact account, then confirm the final Instagram publish."];
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 || request.IdempotencyKey.Any(char.IsControl))
            errors["idempotencyKey"] = ["Provide a stable final-publish idempotency key."];
        if (errors.Count > 0) return (null, errors, false);
        var job = await db.PublishJobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return (null, null, false);
        if (job.Platform != PlatformKind.InstagramReels || string.IsNullOrWhiteSpace(job.RemoteContainerId))
            return (null, new() { ["job"] = ["This job has no prepared Instagram Reel container."] }, true);
        var key = request.IdempotencyKey!.Trim();
        if (!string.IsNullOrWhiteSpace(job.FinalPublishIdempotencyKey))
            return string.Equals(job.FinalPublishIdempotencyKey, key, StringComparison.Ordinal)
                ? (job, null, false)
                : (null, new() { ["idempotencyKey"] = ["This Instagram final publish was already confirmed with a different key."] }, true);
        if (job.Status != PublishStatus.AwaitingFinalConfirmation)
            return (null, new() { ["job"] = ["This Reel is not waiting for final confirmation."] }, true);
        job.FinalPublishIdempotencyKey = key;
        job.FinalPublishConfirmedAt = DateTimeOffset.UtcNow;
        job.Status = PublishStatus.Queued;
        job.Progress = Math.Max(job.Progress, 0.9);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (job, null, false);
    }

    public async Task<(PublishJob? Job, Dictionary<string, string[]>? Errors, bool Conflict)> CompleteManualAsync(
        Guid id, CompleteManualPublishRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.Confirmed) errors["confirmed"] = ["Confirm that you completed the TikTok upload yourself."];
        if (!TryNormalizeTikTokUrl(request.PostUrl, out var postUrl)) errors["postUrl"] = ["Paste the resulting HTTPS TikTok post URL."];
        if (errors.Count > 0) return (null, errors, false);
        var job = await db.PublishJobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return (null, null, false);
        if (job.Platform == PlatformKind.TikTok && job.Status == PublishStatus.Published &&
            string.Equals(job.ExternalUrl, postUrl, StringComparison.OrdinalIgnoreCase)) return (job, null, false);
        if (job.Platform != PlatformKind.TikTok || job.Status != PublishStatus.RequiresManualAction)
            return (null, new() { ["job"] = ["Only a pending manual TikTok handoff can be marked complete."] }, true);
        job.ExternalUrl = postUrl;
        job.Status = PublishStatus.Published;
        job.Progress = 1;
        job.PublishedAt = DateTimeOffset.UtcNow;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        job.ResultJson = JsonSerializer.Serialize(new { mode = "manual", completedByUser = true }, JsonDefaults.Serializer);
        await db.SaveChangesAsync(cancellationToken);
        return (job, null, false);
    }

    internal static string ComputeRequestHash(Guid campaignId, PlatformKind platform, string path, string title,
        string caption, string privacy, bool shareToFeed, bool dryRun, string fileSha256, string accountIdentifier)
    {
        var canonical = JsonSerializer.Serialize(new { campaignId, platform = platform.ToString(), path, title, caption,
            privacy, shareToFeed, dryRun, fileSha256, accountIdentifier }, JsonDefaults.Serializer);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async Task<Dictionary<string, string[]>> ValidatePublishingCopyAsync(Guid campaignId, PlatformKind platform,
        string caption, CancellationToken cancellationToken)
    {
        var profile = await db.PlatformProfiles.AsNoTracking().SingleOrDefaultAsync(
            x => x.CampaignId == campaignId && x.Platform == platform, cancellationToken);
        var missing = new List<string>();
        if (profile is not null)
        {
            foreach (var value in JsonDefaults.Parse(profile.RequiredHashtagsJson, Array.Empty<string>())
                         .Concat(JsonDefaults.Parse(profile.RequiredMentionsJson, Array.Empty<string>()))
                         .Append(profile.DisclosureText).Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!caption.Contains(value!, StringComparison.OrdinalIgnoreCase)) missing.Add(value!);
        }
        return missing.Count == 0 ? [] : new() { ["caption"] = [$"Caption is missing required campaign text: {string.Join(", ", missing)}"] };
    }

    internal static bool TryNormalizeTikTokUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !YouTubePublisher.IsHostOrSubdomain(uri.Host, "tiktok.com")) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }
}

public sealed class PublishingWorker(IServiceScopeFactory scopeFactory, ILogger<PublishingWorker> logger) : BackgroundService
{
    private bool _recoveryCompleted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RecoverInterruptedJobsAsync(stoppingToken); _recoveryCompleted = true; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Publishing recovery is waiting for PostgreSQL ({ErrorType}).", ex.GetType().Name);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_recoveryCompleted) { await RecoverInterruptedJobsAsync(stoppingToken); _recoveryCompleted = true; }
                await ProcessNextAsync(stoppingToken);
                await Task.Delay(750, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError("Publishing worker loop failed with {ErrorType}.", ex.GetType().Name);
                await Task.Delay(2_000, stoppingToken);
            }
        }
    }

    internal async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidateId = await db.PublishJobs.AsNoTracking().Where(x => x.Status == PublishStatus.Queued)
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (!candidateId.HasValue) return;
        var claimed = await db.PublishJobs.Where(x => x.Id == candidateId.Value && x.Status == PublishStatus.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, PublishStatus.Validating)
                .SetProperty(x => x.Progress, 0.02)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (claimed != 1) return;
        var job = await db.PublishJobs.SingleAsync(x => x.Id == candidateId.Value, cancellationToken);
        var remoteOperationStarted = false;
        try
        {
            PublishingMediaValidation? media = null;
            var isInstagramFinal = job.Platform == PlatformKind.InstagramReels &&
                job.FinalPublishConfirmedAt.HasValue && !string.IsNullOrWhiteSpace(job.RemoteContainerId);
            if (!isInstagramFinal)
            {
                var validator = scope.ServiceProvider.GetRequiredService<PublishingMediaValidator>();
                media = await validator.ValidateAsync(job.CampaignId, job.OutputRelativePath, job.Platform, cancellationToken);
                if (!media.Valid) throw new PublishingApiException(media.Error!);
                if (!string.Equals(media.Sha256, job.FileSha256, StringComparison.Ordinal) || media.SizeBytes != job.FileSizeBytes)
                    throw new PublishingApiException("The rendered video changed after confirmation. Create a new publishing attempt and review it again.");
            }
            if (job.Platform == PlatformKind.TikTok)
            {
                job.ManualPackageRelativePath = await CreateManualPackageAsync(scope.ServiceProvider.GetRequiredService<WorkspaceService>(), job, media!.FullPath!, cancellationToken);
                job.Status = PublishStatus.RequiresManualAction; job.Progress = 1; job.UpdatedAt = DateTimeOffset.UtcNow;
                job.ResultJson = JsonSerializer.Serialize(new { mode = "manual", instructions = PlatformPublishingCatalog.Guide(job.Platform).UploadInstructions }, JsonDefaults.Serializer);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            if (job.DryRun)
            {
                job.Status = PublishStatus.Published; job.Progress = 1; job.ExternalId = $"dry-run-{job.Id:N}";
                job.PublishedAt = DateTimeOffset.UtcNow; job.UpdatedAt = DateTimeOffset.UtcNow;
                job.ResultJson = JsonSerializer.Serialize(new { resultCode = "DryRun", externalRequestCount = 0 }, JsonDefaults.Serializer);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            var connectionService = scope.ServiceProvider.GetRequiredService<PlatformConnectionService>();
            var status = await connectionService.GetAsync(job.Platform, cancellationToken);
            if (!status.CanPublish) throw new PublishingApiException(status.CapabilityMessage);
            if (!string.Equals(status.AccountIdentifier, job.AccountIdentifier, StringComparison.Ordinal))
                throw new PublishingApiException("The connected destination account changed after confirmation. Create and review a new publishing attempt.");
            var credentials = await connectionService.GetCredentialsAsync(job.Platform, cancellationToken) ?? new(StringComparer.OrdinalIgnoreCase);
            var progress = new InlineProgress(value => job.Progress = Math.Clamp(value, 0.05, 0.99));
            PlatformPublishResult result;
            if (job.Platform == PlatformKind.InstagramReels)
            {
                var instagram = scope.ServiceProvider.GetRequiredService<InstagramPublisher>();
                if (!isInstagramFinal)
                {
                    job.Status = PublishStatus.Uploading; job.Progress = 0.05; job.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    remoteOperationStarted = true;
                    var prepared = await instagram.PrepareAsync(new(job.Id, media!.FullPath!, job.Title, job.Caption,
                        job.Privacy, job.ShareToFeed, false), credentials, progress, cancellationToken);
                    job.RemoteContainerId = prepared.ContainerId;
                    job.Status = PublishStatus.AwaitingFinalConfirmation;
                    job.Progress = 0.9;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    job.ResultJson = JsonSerializer.Serialize(new { resultCode = "Prepared", finalConfirmationRequired = true }, JsonDefaults.Serializer);
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                job.Status = PublishStatus.Processing; job.Progress = Math.Max(job.Progress, 0.9); job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                remoteOperationStarted = true;
                result = await instagram.FinalizeAsync(job.Id, job.RemoteContainerId!, credentials, progress, cancellationToken);
            }
            else
            {
                var publisher = scope.ServiceProvider.GetServices<IPlatformPublisher>().Single(x => x.Platform == job.Platform);
                job.Status = PublishStatus.Uploading; job.Progress = 0.05; job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                remoteOperationStarted = true;
                result = await publisher.PublishAsync(new(job.Id, media!.FullPath!, job.Title, job.Caption, job.Privacy,
                    job.ShareToFeed, false), credentials, progress, cancellationToken);
            }
            job.Status = PublishStatus.Published; job.Progress = 1; job.ExternalId = result.ExternalId;
            job.ExternalUrl = result.ExternalUrl; job.PublishedAt = DateTimeOffset.UtcNow; job.UpdatedAt = DateTimeOffset.UtcNow;
            job.ResultJson = JsonSerializer.Serialize(new { result.ResultCode }, JsonDefaults.Serializer);
            await db.SaveChangesAsync(cancellationToken);
            if (job.Platform == PlatformKind.YouTubeShorts && credentials.Count > 0)
            {
                try
                {
                    var expires = credentials.TryGetValue("expiresAt", out var value) && DateTimeOffset.TryParse(value, out var parsed) ? parsed : (DateTimeOffset?)null;
                    await connectionService.SaveCredentialSetAsync(job.Platform, credentials, expires, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("YouTube upload {PublishJobId} completed, but refreshed authorization metadata could not be saved ({ErrorType}).",
                        job.Id, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = remoteOperationStarted ? PublishStatus.NeedsAttention : PublishStatus.Cancelled;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            job.Error = remoteOperationStarted
                ? "The app stopped during a remote operation. Check the platform before creating any retry."
                : "Cancelled before a remote operation started.";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            job.Status = remoteOperationStarted ? PublishStatus.NeedsAttention : PublishStatus.Failed;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            job.Error = PlatformConnectionService.CleanError(ex.Message) ?? "Publishing failed.";
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Publishing job {PublishJobId} for {Platform} failed with {ErrorType}.", job.Id, job.Platform, ex.GetType().Name);
        }
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.PublishJobs.Where(x => x.Status == PublishStatus.Validating)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, PublishStatus.Failed)
                .SetProperty(x => x.Error, "The app stopped during local validation. Review the output and create a new attempt.")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        await db.PublishJobs.Where(x => x.Status == PublishStatus.Uploading || x.Status == PublishStatus.Processing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, PublishStatus.NeedsAttention)
                .SetProperty(x => x.Error, "The app previously stopped during a remote operation. Check the platform before creating any retry.")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    private static async Task<string> CreateManualPackageAsync(WorkspaceService workspace, PublishJob job,
        string videoPath, CancellationToken cancellationToken)
    {
        var packageDirectory = workspace.ResolveRelative(Path.Combine("campaigns", job.CampaignId.ToString("N"), "output", "publish-packages"));
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, $"tiktok-{job.Id:N}.zip");
        await using var output = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var videoEntry = archive.CreateEntry(job.FileName, CompressionLevel.NoCompression);
        await using (var entry = videoEntry.Open())
        await using (var input = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            await input.CopyToAsync(entry, cancellationToken);
        await WriteEntryAsync(archive, "caption.txt", job.Caption, cancellationToken);
        await WriteEntryAsync(archive, "instructions.txt", string.Join(Environment.NewLine,
            PlatformPublishingCatalog.Guide(PlatformKind.TikTok).UploadInstructions.Select((x, i) => $"{i + 1}. {x}")), cancellationToken);
        return workspace.ToRelative(packagePath);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private sealed class InlineProgress(Action<double> update) : IProgress<double>
    {
        public void Report(double value) => update(value);
    }
}
