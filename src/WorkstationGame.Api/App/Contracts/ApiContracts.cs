using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Contracts;

public sealed record CreateCampaignRequest(string Name, string? WhopUrl, string? CampaignType, string? Brand, string? Category,
    decimal? PayoutAmount, int? PayoutPerViews, string? PayoutUnit, DateTimeOffset? Deadline, string RawInstructions, string? Notes);
public sealed record UpdateCampaignRequest(string Name, string? WhopUrl, string? CampaignType, string? Brand, string? Category,
    decimal? PayoutAmount, int? PayoutPerViews, string? PayoutUnit, DateTimeOffset? Deadline, string RawInstructions, string? Notes,
    CampaignStatus Status);
public sealed record CampaignSummary(Guid Id, string Name, string? Brand, CampaignStatus Status, DateTimeOffset UpdatedAt,
    int SourceCount, int PlatformCount, int? LatestRequirementVersion, string? CampaignType, string? Category,
    decimal? PayoutAmount, int? PayoutPerViews, string? PayoutUnit, DateTimeOffset? Deadline, string? WhopUrl);
public sealed record CampaignCountsDto(int Total, int Draft, int Ready, int Processing, int NeedsReview, int Completed, int Archived);
public sealed record DashboardDto(CampaignCountsDto CampaignCounts, int ActiveJobs, int FailedJobs, int NeedsReview,
    long StorageUsedBytes, IReadOnlyList<CampaignSummary> RecentCampaigns, IReadOnlyList<JobDto> RecentJobs,
    int CampaignCount, int ReadyCount, int NeedsReviewCount, int FailedJobCount);
public sealed record CampaignDetail(Guid Id, string Name, string? WhopUrl, string CampaignType, string? Brand, string? Category,
    decimal? PayoutAmount, int? PayoutPerViews, string? PayoutUnit, DateTimeOffset? Deadline, string RawInstructions, string? Notes,
    CampaignStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<RequirementRevisionDto> RequirementRevisions, IReadOnlyList<PlatformProfileDto> PlatformProfiles,
    IReadOnlyList<SourceAssetDto> Sources, IReadOnlyList<CampaignDocumentDto> Documents,
    IReadOnlyList<ReferenceVideoDto> ReferenceVideos, CampaignBrandingDto? Branding);
/// <summary>
/// The client logo burned into every clip for a campaign. Sizes are fractions of the output
/// frame so one setting covers every platform preset. <paramref name="LogoCropRect"/> is the
/// FFmpeg "w:h:x:y" box trimming transparent padding off a full-frame logo sheet, measured
/// once on upload.
/// </summary>
public sealed record CampaignBrandingDto(string? LogoRelativePath, string? LogoFileName = null,
    string? LogoCropRect = null, string Position = "Bottom", double WidthFraction = 0.45,
    double MarginFraction = 0.14, double Opacity = 1, bool Enabled = true);
public sealed record UpdateCampaignBrandingRequest(string Position = "Bottom", double WidthFraction = 0.45,
    double MarginFraction = 0.14, double Opacity = 1, bool Enabled = true);
public sealed record CampaignDocumentDto(Guid Id, string FileName, CampaignDocumentKind Kind, long SizeBytes,
    int? PageCount, int ExtractedCharacterCount, DocumentExtractionStatus ExtractionStatus, string? ExtractionError,
    bool IncludeInExtraction, string RelativePath, DateTimeOffset UploadedAt);
public sealed record UpdateCampaignDocumentRequest(bool IncludeInExtraction);
public sealed record ReferenceVideoDto(Guid Id, string Label, string? Url, string? LocalRelativePath, string? Notes,
    bool IsPrimary, double? DurationSeconds, ReferenceVideoStatus Status, string? Error, JsonElement? StyleProfile,
    DateTimeOffset CreatedAt, DateTimeOffset? AnalyzedAt);
public sealed record AddReferenceVideoRequest(string Url, string? Label = null, string? Notes = null, bool IsPrimary = false);
public sealed record UpdateReferenceVideoRequest(string? Label, string? Notes, bool IsPrimary);
public sealed record RequirementRevisionDto(Guid Id, int Version, bool IsApproved, DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt, IReadOnlyList<RequirementItemDto> Items);
public sealed record RequirementItemDto(Guid Id, string Category, string Key, string Value, string SourceText, double Confidence, bool NeedsReview);
public sealed record RequirementItemInput(string Category, string Key, string Value, string SourceText, double Confidence, bool NeedsReview);
public sealed record UpdateRequirementRevisionRequest(IReadOnlyList<RequirementItemInput>? Items);
public sealed record PlatformProfileDto(Guid Id, PlatformKind Platform, bool Enabled, int Width, int Height,
    double MinDurationSeconds, double PreferredDurationSeconds, double MaxDurationSeconds, long? MaxFileSizeBytes,
    string CaptionTemplate, string[] RequiredHashtags, string[] RequiredMentions, string? DisclosureText,
    string? CallToAction, bool BurnSubtitles, string SafeAreaNotes, string[] ComplianceChecklist,
    Dictionary<string, string> ValueOrigins, DateTimeOffset UpdatedAt);
public sealed record UpdatePlatformProfileRequest(bool Enabled, int Width, int Height, double MinDurationSeconds,
    double PreferredDurationSeconds, double MaxDurationSeconds, long? MaxFileSizeBytes, string CaptionTemplate,
    string[]? RequiredHashtags, string[]? RequiredMentions, string? DisclosureText, string? CallToAction,
    bool BurnSubtitles, string SafeAreaNotes, string[]? ComplianceChecklist);
public sealed record DetectedLinkDto(string Url, string Host, string Kind, string Context, int Occurrences,
    bool AlreadySource, bool AlreadyReference);
public sealed record ImportLinksRequest(IReadOnlyList<string> Urls, string Target, bool ConfirmedPermission, bool Download = true);
public sealed record ImportLinksResult(int Added, int Skipped, int Queued, IReadOnlyList<string> Errors);
public sealed record LocalSourceScanDto(int Attached, int Added, int AlreadyKnown, string Folder, IReadOnlyList<string> Notes);
public sealed record RenderedClipDto(string RelativePath, string FileName, string Platform, long SizeBytes,
    DateTimeOffset CreatedAt, Guid? EditPlanId, double? StartSeconds, double? EndSeconds);
public sealed record AddSourceRequest(string Url);
public sealed record InspectSourceRequest(string Url);
public sealed record StartDownloadRequest(Guid SourceAssetId, string? Quality = null, bool Refresh = false);
public sealed record SourceAssetDto(Guid Id, string Url, string? Title, string? Uploader, double? DurationSeconds,
    string? ThumbnailUrl, string? LocalRelativePath, string? SubtitleRelativePath, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? DownloadedAt, bool IsLocalFile, bool HasWordTranscript);
public sealed record StartAiAnalysisRequest(Guid SourceAssetId, AiMode? Mode = null, PlatformKind Platform = PlatformKind.TikTok,
    string? Transcript = null);
public sealed record SoundEffectPlacementDto(Guid AssetId, double AtSeconds, double GainDb = -6);
public sealed record OverlayDto(string Text, double StartSeconds, double EndSeconds, string Position = "Top", int FontSize = 48, string Color = "white");
/// <summary>Full-frame colour wash on a reaction beat; reference edits run 0.4-0.7s.</summary>
public sealed record ColorFlashDto(double AtSeconds, double DurationSeconds = 0.5, double Intensity = 0.45, string Color = "Green");
/// <summary>Punch-in on a beat; reference edits run 1.1x-2.3x over 0.3-1.0s.</summary>
public sealed record ZoomPunchDto(double AtSeconds, double DurationSeconds = 0.5, double Scale = 1.25);
public sealed record EditPlanExtrasDto(string Framing, string? CtaOverlayText, string? SubtitleStyle,
    double MusicDuckingDb, IReadOnlyList<SoundEffectPlacementDto> SoundEffects, IReadOnlyList<OverlayDto> Overlays,
    IReadOnlyList<ColorFlashDto>? ColorFlashes = null, IReadOnlyList<ZoomPunchDto>? ZoomPunches = null);
public sealed record CreateEditPlanRequest(Guid SourceAssetId, PlatformKind Platform, double StartSeconds, double EndSeconds,
    int? Width, int? Height, int? FramesPerSecond, int? Crf, bool NormalizeAudio, bool BurnSubtitles,
    string? SubtitleRelativePath, Guid? MusicAssetId, double? MusicVolume, string? OutputFileName, bool IsApproved,
    string? Framing = null, string? CtaOverlayText = null, string? SubtitleStyle = null, double? MusicDuckingDb = null,
    IReadOnlyList<SoundEffectPlacementDto>? SoundEffects = null, IReadOnlyList<OverlayDto>? Overlays = null,
    IReadOnlyList<ColorFlashDto>? ColorFlashes = null, IReadOnlyList<ZoomPunchDto>? ZoomPunches = null);
public sealed record UpdateEditPlanRequest(double StartSeconds, double EndSeconds, int Width, int Height, int FramesPerSecond,
    int Crf, bool NormalizeAudio, bool BurnSubtitles, string? SubtitleRelativePath, Guid? MusicAssetId,
    double MusicVolume, string OutputFileName, bool IsApproved, string? Framing = null, string? CtaOverlayText = null,
    string? SubtitleStyle = null, double? MusicDuckingDb = null,
    IReadOnlyList<SoundEffectPlacementDto>? SoundEffects = null, IReadOnlyList<OverlayDto>? Overlays = null,
    IReadOnlyList<ColorFlashDto>? ColorFlashes = null, IReadOnlyList<ZoomPunchDto>? ZoomPunches = null);
public sealed record ApproveEditPlanRequest(bool Approved = true);
public sealed record EditPlanDto(Guid Id, Guid CampaignId, Guid SourceAssetId, PlatformKind Platform, double StartSeconds,
    double EndSeconds, int Width, int Height, int FramesPerSecond, int Crf, bool NormalizeAudio,
    bool BurnSubtitles, string? SubtitleRelativePath, Guid? MusicAssetId, double MusicVolume,
    string OutputFileName, bool IsApproved, string Framing, string? CtaOverlayText, string? SubtitleStyle,
    double MusicDuckingDb, IReadOnlyList<SoundEffectPlacementDto> SoundEffects,
    IReadOnlyList<OverlayDto> Overlays, DateTimeOffset CreatedAt,
    IReadOnlyList<ColorFlashDto>? ColorFlashes = null, IReadOnlyList<ZoomPunchDto>? ZoomPunches = null);
public sealed record JobDto(Guid Id, Guid? CampaignId, JobKind Kind, JobStatus Status, double Progress,
    string? ResultJson, JsonElement? Result, string? Error, string? CampaignName, int? ExitCode, int Attempt, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
public sealed record AddMediaLocationRequest(string Path, MediaAssetKind Kind);
public sealed record UpdateMediaAssetRequest(string[]? Tags, string? Mood, string? Energy, string? Genre, double? Bpm, bool Favorite, string? LicenseNotes);
public sealed record MediaLocationDto(Guid Id, string Path, MediaAssetKind Kind, bool Enabled, DateTimeOffset CreatedAt);
public sealed record MediaAssetDto(Guid Id, Guid LocationId, string FileName, MediaAssetKind Kind, long SizeBytes,
    string ContentHash, double? DurationSeconds, string[] Tags, string? Mood, string? Energy, string? Genre,
    double? Bpm, bool Favorite, string? LicenseNotes, DateTimeOffset IndexedAt);
public sealed record UpdateSettingsRequest(string WorkspaceRoot, string YtDlpPath, string FfmpegPath,
    string FfprobePath, string ClaudePath, string CodexPath, int ProcessTimeoutMinutes,
    AiMode DefaultAiMode = AiMode.Compare, int MaxConcurrentMediaJobs = 1, bool RequireEditApproval = true,
    string PreferredVideoQuality = "Best", string? OverlayFontPath = null,
    string? CookiesFromBrowser = null, string? CookiesFilePath = null, string? JsRuntime = null);
public sealed record SettingsDto(string WorkspaceRoot, string WorkspacePath, string YtDlpPath, string FfmpegPath, string FfprobePath,
    string ClaudePath, string CodexPath, int ProcessTimeoutMinutes, int AiTimeoutMinutes, AiMode DefaultAiMode,
    int MaxConcurrentMediaJobs, bool RequireEditApproval, string PreferredVideoQuality,
    bool RestartRequiredToApplyChanges = false, string? OverlayFontPath = null,
    string? CookiesFromBrowser = null, string? CookiesFilePath = null, string? JsRuntime = null);
public sealed record ToolDependencyDto(string Name, string ConfiguredPath, bool Available, string? Version, string? Error);
public sealed record AiHighlightDto(Guid Id, string Provider, int Rank, double StartSeconds, double EndSeconds,
    string Hook, string Rationale, double Score, double Confidence, bool IsSelected, JsonElement EditPlan);
public sealed record AiAnalysisDto(Guid Id, Guid CampaignId, Guid SourceAssetId, Guid JobId, AiMode Mode, string Status,
    string[] Providers, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, IReadOnlyList<AiHighlightDto> Highlights);

public sealed class CreateCampaignValidator : AbstractValidator<CreateCampaignRequest>
{
    public CreateCampaignValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.WhopUrl).Must(BeHttpUrl).When(x => !string.IsNullOrWhiteSpace(x.WhopUrl)).WithMessage("WhopUrl must be an http(s) URL.");
        RuleFor(x => x.RawInstructions).MaximumLength(200_000);
        RuleFor(x => x.PayoutAmount).InclusiveBetween(0, 1_000_000).When(x => x.PayoutAmount.HasValue);
        RuleFor(x => x.PayoutPerViews).InclusiveBetween(1, 100_000_000).When(x => x.PayoutPerViews.HasValue);
    }
    private static bool BeHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public sealed class UpdateCampaignValidator : AbstractValidator<UpdateCampaignRequest>
{
    public UpdateCampaignValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.RawInstructions).MaximumLength(200_000);
        RuleFor(x => x.WhopUrl).Must(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .When(x => !string.IsNullOrWhiteSpace(x.WhopUrl)).WithMessage("WhopUrl must be an http(s) URL.");
        RuleFor(x => x.PayoutAmount).InclusiveBetween(0, 1_000_000).When(x => x.PayoutAmount.HasValue);
        RuleFor(x => x.PayoutPerViews).InclusiveBetween(1, 100_000_000).When(x => x.PayoutPerViews.HasValue);
    }
}

public sealed class AddSourceValidator : AbstractValidator<AddSourceRequest>
{
    public AddSourceValidator() => RuleFor(x => x.Url).Must(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        .WithMessage("Only absolute http(s) source URLs are accepted.");
}

public sealed class AddReferenceVideoValidator : AbstractValidator<AddReferenceVideoRequest>
{
    public AddReferenceVideoValidator()
    {
        RuleFor(x => x.Url).Must(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .WithMessage("Only absolute http(s) reference URLs are accepted.");
        RuleFor(x => x.Label).MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(4_000);
    }
}

public sealed class UpdateReferenceVideoValidator : AbstractValidator<UpdateReferenceVideoRequest>
{
    public UpdateReferenceVideoValidator()
    {
        RuleFor(x => x.Label).MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(4_000);
    }
}

public sealed class CreateEditPlanValidator : AbstractValidator<CreateEditPlanRequest>
{
    public CreateEditPlanValidator()
    {
        RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0);
        RuleFor(x => x.EndSeconds).GreaterThan(x => x.StartSeconds).LessThanOrEqualTo(86_400);
        RuleFor(x => x.Width).InclusiveBetween(240, 7680).When(x => x.Width.HasValue);
        RuleFor(x => x.Height).InclusiveBetween(240, 7680).When(x => x.Height.HasValue);
        RuleFor(x => x.FramesPerSecond).InclusiveBetween(12, 120).When(x => x.FramesPerSecond.HasValue);
        RuleFor(x => x.Crf).InclusiveBetween(0, 51).When(x => x.Crf.HasValue);
        RuleFor(x => x.MusicVolume).InclusiveBetween(0, 1).When(x => x.MusicVolume.HasValue);
        RuleFor(x => x.MusicDuckingDb).InclusiveBetween(-40, 0).When(x => x.MusicDuckingDb.HasValue);
        RuleFor(x => x.Framing).Must(IsFraming).When(x => !string.IsNullOrWhiteSpace(x.Framing));
        RuleFor(x => x.CtaOverlayText).MaximumLength(500);
        RuleFor(x => x.OutputFileName).Must(IsSafeOutputName).When(x => !string.IsNullOrWhiteSpace(x.OutputFileName));
        RuleFor(x => x.SubtitleRelativePath).Must(IsSafeSubtitlePath).When(x => !string.IsNullOrWhiteSpace(x.SubtitleRelativePath));
        RuleForEach(x => x.SoundEffects).ChildRules(effect =>
        {
            effect.RuleFor(x => x.AtSeconds).GreaterThanOrEqualTo(0);
            effect.RuleFor(x => x.GainDb).InclusiveBetween(-60, 12);
        }).When(x => x.SoundEffects is not null);
        RuleForEach(x => x.Overlays).ChildRules(overlay =>
        {
            overlay.RuleFor(x => x.Text).NotEmpty().MaximumLength(500);
            overlay.RuleFor(x => x.EndSeconds).GreaterThan(x => x.StartSeconds);
            overlay.RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0);
            overlay.RuleFor(x => x.FontSize).InclusiveBetween(12, 200);
            overlay.RuleFor(x => x.Position).Must(IsPosition);
            overlay.RuleFor(x => x.Color).Must(IsSafeColor);
        }).When(x => x.Overlays is not null);
        RuleFor(x => x).Must(x => (x.SoundEffects ?? []).All(e => e.AtSeconds <= x.EndSeconds - x.StartSeconds))
            .WithMessage("Sound effect timestamps must be within the clip.");
        RuleFor(x => x).Must(x => (x.Overlays ?? []).All(e => e.EndSeconds <= x.EndSeconds - x.StartSeconds))
            .WithMessage("Overlay timestamps must be within the clip.");
    }

    internal static bool IsFraming(string? value) => value is "CenterCrop" or "SmartCrop" or "Fit" or "Letterbox";
    internal static bool IsPosition(string value) => value is "Top" or "Center" or "Bottom";
    internal static bool IsSafeColor(string value) => !string.IsNullOrWhiteSpace(value) &&
        (value[0] == '#' ? value.Length is 7 or 9 && value[1..].All(Uri.IsHexDigit) : value.Length <= 20 && value.All(char.IsLetter));
    internal static bool IsSafeOutputName(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Path.GetFileName(value) == value && Path.GetExtension(value).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    internal static bool IsSafeSubtitlePath(string? value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) &&
        Path.GetExtension(value).ToLowerInvariant() is ".srt" or ".ass" or ".vtt";
}

public sealed class UpdateEditPlanValidator : AbstractValidator<UpdateEditPlanRequest>
{
    public UpdateEditPlanValidator()
    {
        RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0);
        RuleFor(x => x.EndSeconds).GreaterThan(x => x.StartSeconds).LessThanOrEqualTo(86_400);
        RuleFor(x => x.Width).InclusiveBetween(240, 7680);
        RuleFor(x => x.Height).InclusiveBetween(240, 7680);
        RuleFor(x => x.FramesPerSecond).InclusiveBetween(12, 120);
        RuleFor(x => x.Crf).InclusiveBetween(0, 51);
        RuleFor(x => x.MusicVolume).InclusiveBetween(0, 1);
        RuleFor(x => x.MusicDuckingDb).InclusiveBetween(-40, 0).When(x => x.MusicDuckingDb.HasValue);
        RuleFor(x => x.Framing).Must(CreateEditPlanValidator.IsFraming).When(x => !string.IsNullOrWhiteSpace(x.Framing));
        RuleFor(x => x.CtaOverlayText).MaximumLength(500);
        RuleFor(x => x.OutputFileName).Must(CreateEditPlanValidator.IsSafeOutputName);
        RuleFor(x => x.SubtitleRelativePath).Must(CreateEditPlanValidator.IsSafeSubtitlePath)
            .When(x => !string.IsNullOrWhiteSpace(x.SubtitleRelativePath));
        RuleForEach(x => x.SoundEffects).ChildRules(effect =>
        {
            effect.RuleFor(x => x.AtSeconds).GreaterThanOrEqualTo(0);
            effect.RuleFor(x => x.GainDb).InclusiveBetween(-60, 12);
        }).When(x => x.SoundEffects is not null);
        RuleForEach(x => x.Overlays).ChildRules(overlay =>
        {
            overlay.RuleFor(x => x.Text).NotEmpty().MaximumLength(500);
            overlay.RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0);
            overlay.RuleFor(x => x.EndSeconds).GreaterThan(x => x.StartSeconds);
            overlay.RuleFor(x => x.FontSize).InclusiveBetween(12, 200);
            overlay.RuleFor(x => x.Position).Must(CreateEditPlanValidator.IsPosition);
            overlay.RuleFor(x => x.Color).Must(CreateEditPlanValidator.IsSafeColor);
        }).When(x => x.Overlays is not null);
        RuleFor(x => x).Must(x => (x.SoundEffects ?? []).All(e => e.AtSeconds <= x.EndSeconds - x.StartSeconds));
        RuleFor(x => x).Must(x => (x.Overlays ?? []).All(e => e.EndSeconds <= x.EndSeconds - x.StartSeconds));
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Serializer = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string StringArray(IEnumerable<string> values) => JsonSerializer.Serialize(values.Distinct(StringComparer.OrdinalIgnoreCase), Serializer);
    public static T Parse<T>(string? value, T fallback)
    {
        try { return string.IsNullOrWhiteSpace(value) ? fallback : JsonSerializer.Deserialize<T>(value, Serializer) ?? fallback; }
        catch (JsonException) { return fallback; }
    }
    public static JsonElement? ParseElement(string? value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? null : JsonDocument.Parse(value).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
}

public static class ApiMappings
{
    public static CampaignSummary ToSummary(this Campaign value) => new(value.Id, value.Name, value.Brand, value.Status,
        value.UpdatedAt, value.Sources.Count, value.PlatformProfiles.Count,
        value.RequirementRevisions.Count == 0 ? null : value.RequirementRevisions.Max(x => x.Version),
        value.CampaignType, value.Category, value.PayoutAmount, value.PayoutPerViews, value.PayoutUnit,
        value.Deadline, value.WhopUrl);

    public static CampaignDetail ToDetail(this Campaign value) => new(value.Id, value.Name, value.WhopUrl, value.CampaignType,
        value.Brand, value.Category, value.PayoutAmount, value.PayoutPerViews, value.PayoutUnit, value.Deadline, value.RawInstructions,
        value.Notes, value.Status, value.CreatedAt, value.UpdatedAt,
        value.RequirementRevisions.OrderByDescending(x => x.Version).Select(x => new RequirementRevisionDto(x.Id, x.Version,
            x.IsApproved, x.CreatedAt, x.ApprovedAt, x.Items.Select(i => new RequirementItemDto(i.Id, i.Category, i.Key,
                i.Value, i.SourceText, i.Confidence, i.NeedsReview)).ToList())).ToList(),
        value.PlatformProfiles.OrderBy(x => x.Platform).Select(ToDto).ToList(),
        value.Sources.OrderByDescending(x => x.CreatedAt).Select(ToDto).ToList(),
        value.Documents.OrderBy(x => x.UploadedAt).Select(ToDto).ToList(),
        value.ReferenceVideos.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.CreatedAt).Select(ToDto).ToList(),
        value.ReadBranding());

    /// <summary>Null until a logo is uploaded; there is nothing to burn in before that.</summary>
    public static CampaignBrandingDto? ReadBranding(this Campaign value) =>
        string.IsNullOrWhiteSpace(value.BrandingJson) ? null : JsonDefaults.Parse<CampaignBrandingDto?>(value.BrandingJson, null);

    public static CampaignDocumentDto ToDto(this CampaignDocument value) => new(value.Id, value.FileName, value.Kind,
        value.SizeBytes, value.PageCount, value.ExtractedCharacterCount, value.ExtractionStatus, value.ExtractionError,
        value.IncludeInExtraction, value.RelativePath, value.UploadedAt);

    public static ReferenceVideoDto ToDto(this ReferenceVideo value) => new(value.Id, value.Label, value.Url,
        value.LocalRelativePath, value.Notes, value.IsPrimary, value.DurationSeconds, value.Status, value.Error,
        JsonDefaults.ParseElement(value.StyleProfileJson), value.CreatedAt, value.AnalyzedAt);

    public static PlatformProfileDto ToDto(this PlatformProfile value) => new(value.Id, value.Platform, value.Enabled,
        value.Width, value.Height, value.MinDurationSeconds, value.PreferredDurationSeconds, value.MaxDurationSeconds,
        value.MaxFileSizeBytes, value.CaptionTemplate, JsonDefaults.Parse(value.RequiredHashtagsJson, Array.Empty<string>()),
        JsonDefaults.Parse(value.RequiredMentionsJson, Array.Empty<string>()), value.DisclosureText, value.CallToAction,
        value.BurnSubtitles, value.SafeAreaNotes, JsonDefaults.Parse(value.ComplianceChecklistJson, Array.Empty<string>()),
        JsonDefaults.Parse(value.ValueOriginsJson, new Dictionary<string, string>()), value.UpdatedAt);

    public static SourceAssetDto ToDto(this SourceAsset value) => new(value.Id, value.Url, value.Title, value.Uploader,
        value.DurationSeconds, value.ThumbnailUrl, value.LocalRelativePath, value.SubtitleRelativePath,
        value.DownloadedAt.HasValue ? "Downloaded" : value.Title is not null ? "Inspected" : "Pending", value.CreatedAt,
        value.DownloadedAt, string.IsNullOrWhiteSpace(value.Url),
        !string.IsNullOrWhiteSpace(value.WordTranscriptRelativePath));

    public static JobDto ToDto(this JobRecord value, string? campaignName = null) => new(value.Id, value.CampaignId, value.Kind, value.Status,
        value.Progress, value.ResultJson, JsonDefaults.ParseElement(value.ResultJson), value.Error, campaignName,
        value.ExitCode, value.Attempt, value.CreatedAt, value.StartedAt, value.CompletedAt);

    public static EditPlanDto ToDto(this EditPlan value)
    {
        var extras = JsonDefaults.Parse(value.ExtraSettingsJson, new EditPlanExtrasDto("CenterCrop", null, null, -12, [], []));
        return new(value.Id, value.CampaignId, value.SourceAssetId,
        value.Platform, value.StartSeconds, value.EndSeconds, value.Width, value.Height, value.FramesPerSecond,
        value.Crf, value.NormalizeAudio, value.BurnSubtitles, value.SubtitleRelativePath, value.MusicAssetId,
        value.MusicVolume, value.OutputFileName, value.IsApproved, extras.Framing, extras.CtaOverlayText,
        extras.SubtitleStyle, extras.MusicDuckingDb, extras.SoundEffects,
        extras.Overlays, value.CreatedAt, extras.ColorFlashes, extras.ZoomPunches);
    }

    public static MediaAssetDto ToDto(this MediaLibraryAsset value) => new(value.Id, value.LocationId, value.FileName,
        value.Kind, value.SizeBytes, value.ContentHash, value.DurationSeconds, JsonDefaults.Parse(value.TagsJson, Array.Empty<string>()),
        value.Mood, value.Energy, value.Genre, value.Bpm, value.IsFavorite, value.LicenseNotes, value.IndexedAt);
}
