namespace WhopClipper.Api.App.Domain;

public enum CampaignStatus { Draft, Ready, Processing, NeedsReview, Completed, Archived }
public enum PlatformKind { TikTok, InstagramReels, YouTubeShorts, X }
public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }
public enum JobKind { InspectSource, Download, AiAnalysis, Render, Validate, ReferenceAnalysis, Transcribe, SourcePipeline, BrollRepair,
    FootageDownload, DriveUpload, DriveReturn }
public enum AiMode { Claude, Codex, Auto, Compare }
public enum MediaAssetKind { Music, SoundEffect }
// Html is appended rather than inserted: these are stored as integers, so an earlier position
// would silently retype every document already in the database.
public enum CampaignDocumentKind { Pdf, Word, Text, Markdown, Html }
public enum DocumentExtractionStatus { Pending, Extracted, Failed }
public enum ReferenceVideoStatus { Pending, Analyzing, Analyzed, Failed }
public enum PublishingConnectionMode { Api, Manual }
public enum PublishingConnectionStatus { NotConfigured, Configured, NeedsAttention }
public enum PublishStatus
{
    Queued,
    Validating,
    Uploading,
    Processing,
    AwaitingFinalConfirmation,
    Published,
    RequiresManualAction,
    NeedsAttention,
    Failed,
    Cancelled,
    /// <summary>Uploaded private to YouTube with a publishAt; YouTube itself makes it public at the slot.</summary>
    Scheduled,
    /// <summary>Taken off the schedule: deleted from YouTube, or cancelled before it was uploaded.</summary>
    Removed
}

public sealed class Campaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? WhopUrl { get; set; }
    public string CampaignType { get; set; } = "Clipping";
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public decimal? PayoutAmount { get; set; }
    /// <summary>View count the payout applies to, e.g. 1000 for "$1 per 1,000 views". Null for rewards that are not view-based.</summary>
    public int? PayoutPerViews { get; set; }
    public string? PayoutUnit { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public string RawInstructions { get; set; } = string.Empty;
    public string? Notes { get; set; }
    /// <summary>Serialized <c>CampaignBrandingDto</c>: the client logo burned into every clip, and where it sits.</summary>
    public string? BrandingJson { get; set; }
    /// <summary>Serialized <c>CampaignEditStyleDto</c>: which edit the campaign's clips get. Null is the streamer style.</summary>
    public string? EditStyleJson { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<CampaignRequirementRevision> RequirementRevisions { get; set; } = [];
    public ICollection<PlatformProfile> PlatformProfiles { get; set; } = [];
    public ICollection<SourceAsset> Sources { get; set; } = [];
    public ICollection<CampaignDocument> Documents { get; set; } = [];
    public ICollection<ReferenceVideo> ReferenceVideos { get; set; } = [];
    public ICollection<PlatformAccountKit> PlatformAccountKits { get; set; } = [];
    public ICollection<PublishJob> PublishJobs { get; set; } = [];
}

/// <summary>An uploaded PDF/Word/text brief kept in the campaign workspace and folded into requirement extraction.</summary>
public sealed class CampaignDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public CampaignDocumentKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
    public int ExtractedCharacterCount { get; set; }
    public int? PageCount { get; set; }
    public DocumentExtractionStatus ExtractionStatus { get; set; } = DocumentExtractionStatus.Pending;
    public string? ExtractionError { get; set; }
    public bool IncludeInExtraction { get; set; } = true;
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A campaign style reference. Locally derived measurements drive the edit plan; the media file itself never leaves the machine.</summary>
public sealed class ReferenceVideo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public string Label { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? LocalRelativePath { get; set; }
    public string? SubtitleRelativePath { get; set; }
    public string? Notes { get; set; }
    public bool IsPrimary { get; set; }
    public double? DurationSeconds { get; set; }
    public ReferenceVideoStatus Status { get; set; } = ReferenceVideoStatus.Pending;
    public string? StyleProfileJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnalyzedAt { get; set; }
}

public sealed class CampaignRequirementRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public int Version { get; set; }
    public string RawInstructions { get; set; } = string.Empty;
    public bool IsApproved { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public ICollection<RequirementItem> Items { get; set; } = [];
}

public sealed class RequirementItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public CampaignRequirementRevision Revision { get; set; } = null!;
    public string Category { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public bool NeedsReview { get; set; }
}

public sealed class PlatformProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public PlatformKind Platform { get; set; }
    public bool Enabled { get; set; } = true;
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 1920;
    public double MinDurationSeconds { get; set; } = 15;
    public double PreferredDurationSeconds { get; set; } = 30;
    public double MaxDurationSeconds { get; set; } = 60;
    public long? MaxFileSizeBytes { get; set; }
    public string CaptionTemplate { get; set; } = string.Empty;
    public string RequiredHashtagsJson { get; set; } = "[]";
    public string RequiredMentionsJson { get; set; } = "[]";
    public string? DisclosureText { get; set; }
    public string? CallToAction { get; set; }
    public bool BurnSubtitles { get; set; } = true;
    public string SafeAreaNotes { get; set; } = "Keep key text within the central 80% of the frame.";
    public string ComplianceChecklistJson { get; set; } = "[]";
    public string ValueOriginsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SourceAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public string Url { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Uploader { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? LocalRelativePath { get; set; }
    public string? SubtitleRelativePath { get; set; }
    /// <summary>Word-level transcript JSON produced by faster-whisper, used for captions.</summary>
    public string? WordTranscriptRelativePath { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DownloadedAt { get; set; }
}

public sealed class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CampaignId { get; set; }
    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public double Progress { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AiAnalysisRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid SourceAssetId { get; set; }
    public Guid JobId { get; set; }
    public AiMode Mode { get; set; }
    public string ProvidersJson { get; set; } = "[]";
    public string Status { get; set; } = "Queued";
    public string? RawOutputJson { get; set; }
    public string? Error { get; set; }
    public string PromptVersion { get; set; } = "1.0";
    public string SchemaVersion { get; set; } = "1.0";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<HighlightCandidate> Highlights { get; set; } = [];
}

public sealed class HighlightCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AiAnalysisRunId { get; set; }
    public AiAnalysisRun AnalysisRun { get; set; } = null!;
    public string Provider { get; set; } = string.Empty;
    public int Rank { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Hook { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public double Score { get; set; }
    public double Confidence { get; set; }
    public string EditPlanJson { get; set; } = "{}";
    public bool IsSelected { get; set; }
}

public sealed class EditPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid SourceAssetId { get; set; }
    public PlatformKind Platform { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 1920;
    public int FramesPerSecond { get; set; } = 30;
    public int Crf { get; set; } = 20;
    public bool NormalizeAudio { get; set; } = true;
    public bool BurnSubtitles { get; set; }
    public string? SubtitleRelativePath { get; set; }
    public Guid? MusicAssetId { get; set; }
    public double MusicVolume { get; set; } = 0.12;
    public string OutputFileName { get; set; } = "clip.mp4";
    public string? ExtraSettingsJson { get; set; }
    public bool IsApproved { get; set; }
    public string? LastFfmpegArgumentsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaLibraryLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public MediaAssetKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaLibraryAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LocationId { get; set; }
    public MediaLibraryLocation Location { get; set; } = null!;
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public MediaAssetKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public string TagsJson { get; set; } = "[]";
    public string? Mood { get; set; }
    public string? Energy { get; set; }
    public string? Genre { get; set; }
    public double? Bpm { get; set; }
    public bool IsFavorite { get; set; }
    public string? LicenseNotes { get; set; }
    /// <summary>The part of the track to play under a clip, from the folder's segment guide.
    /// Null means start at the top, which is what every asset indexed before the guide existed does.</summary>
    public double? SegmentStartSeconds { get; set; }
    public double? SegmentEndSeconds { get; set; }
    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApplicationSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Copy-ready, non-secret account/profile data. Passwords, OTPs and recovery codes are never accepted.</summary>
public sealed class PlatformAccountKit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public PlatformKind Platform { get; set; }
    public string FieldsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Non-secret OAuth metadata. Secret material lives in the current user's separate local vault.</summary>
public sealed class PlatformConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PlatformKind Platform { get; set; }
    public string CredentialReference { get; set; } = string.Empty;
    public string ConfiguredFieldsJson { get; set; } = "[]";
    public string SecretFlagsJson { get; set; } = "{}";
    public string GrantedScopesJson { get; set; } = "[]";
    public string? AccountIdentifier { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A durable, one-shot publishing attempt. Completed attempts are never automatically retried.</summary>
public sealed class PublishJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public PlatformKind Platform { get; set; }
    public PublishStatus Status { get; set; } = PublishStatus.Queued;
    public double Progress { get; set; }
    public string OutputRelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public string Privacy { get; set; } = "private";
    public bool ShareToFeed { get; set; } = true;
    public bool DryRun { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? AccountIdentifier { get; set; }
    public string? RemoteContainerId { get; set; }
    public string? FinalPublishIdempotencyKey { get; set; }
    public DateTimeOffset? FinalPublishConfirmedAt { get; set; }
    public string? ExternalId { get; set; }
    public string? ExternalUrl { get; set; }
    public string? Error { get; set; }
    public string? ResultJson { get; set; }
    public string? ManualPackageRelativePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>The clip this attempt publishes, so the gallery can show its slot.</summary>
    public Guid? EditPlanId { get; set; }
    /// <summary>The daily slot at which YouTube makes the video public. Null for an immediate upload.</summary>
    public DateTimeOffset? ScheduledPublishAt { get; set; }
    /// <summary>A queued scheduled upload waits until this time (its slot is near, or the quota reset).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>The go-public time the phone notification is currently scheduled for, if any.</summary>
    public DateTimeOffset? NotificationScheduledFor { get; set; }
}
