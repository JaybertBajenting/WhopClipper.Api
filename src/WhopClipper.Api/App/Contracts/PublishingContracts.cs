using System.Text.Json;
using System.Text.Json.Serialization;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Contracts;

public sealed record PublishingCredentialRequirementDto(string Key, string Label, bool Secret, bool Required, string HelpText);
public sealed record PlatformPublishingGuideDto(PlatformKind Platform, string DisplayName,
    PublishingConnectionMode ConnectionMode, string SignupUrl, string AccountTypeRequirement,
    IReadOnlyList<PublishingCredentialRequirementDto> CredentialRequirements,
    IReadOnlyList<string> VideoRequirements, IReadOnlyList<string> UploadInstructions,
    IReadOnlyList<string> Limitations);

public sealed record AccountKitFieldDto(string Key, string Label, string Value, bool Required, bool Copyable, string HelpText);
public sealed record PlatformPublishDefaultsDto(bool Enabled, string Title, string Caption, string[] Hashtags,
    string[] Mentions, string? Disclosure, string? CallToAction, string Privacy);
public sealed record PlatformAccountKitDto(Guid CampaignId, PlatformKind Platform, string DisplayName,
    PublishingConnectionMode ConnectionMode, string SignupUrl, IReadOnlyList<AccountKitFieldDto> Fields,
    IReadOnlyList<string> SetupSteps, PlatformPublishDefaultsDto PublishDefaults);
public sealed record SavePlatformAccountKitRequest(Dictionary<string, string>? Fields);

/// <summary>Write-only secret input. Unknown and sensitive account-login fields are rejected, not ignored.</summary>
public sealed class SavePlatformConnectionRequest
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? AccountId { get; init; }
    public string? PageId { get; init; }
    public string? AuthMode { get; init; }
    public DateTimeOffset? TokenExpiresAt { get; init; }
    public string[]? ClearFields { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}

public sealed record PlatformConnectionDto(PlatformKind Platform, PublishingConnectionMode ConnectionMode,
    PublishingConnectionStatus Status, IReadOnlyList<string> ConfiguredFields,
    IReadOnlyDictionary<string, bool> SecretConfigured, string? AccountIdentifier,
    IReadOnlyList<string> GrantedScopes,
    DateTimeOffset? TokenExpiresAt, DateTimeOffset? UpdatedAt, DateTimeOffset? LastValidatedAt, string? LastError,
    bool CanPublish, string CapabilityMessage);
public sealed record PublishingSessionDto(string Nonce, string HeaderName);
public sealed record OAuthStartResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);

public sealed record CreatePublishJobRequest(PlatformKind Platform, string OutputRelativePath, string Title,
    string Caption, string Privacy = "private", bool ShareToFeed = true, bool Confirmed = false,
    string? IdempotencyKey = null, bool DryRun = false);
public sealed record ConfirmInstagramPublishRequest(bool Confirmed, string? IdempotencyKey);
public sealed record CompleteManualPublishRequest(bool Confirmed, string? PostUrl);
public sealed record PublishJobDto(Guid Id, Guid CampaignId, PlatformKind Platform, PublishStatus Status,
    double Progress, string OutputRelativePath, string FileName, string Title, string Caption, string Privacy,
    bool ShareToFeed, bool DryRun, string? AccountIdentifier, string FileSha256,
    string? ExternalId, string? ExternalUrl, string? Error, string? ManualPackageDownloadUrl,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt,
    Guid? EditPlanId = null, DateTimeOffset? ScheduledPublishAt = null, DateTimeOffset? UploadsAfter = null,
    bool NotificationScheduled = false);

/// <summary>A scheduled YouTube upload: its go-public slot and when the upload itself may start.</summary>
public sealed record ScheduledUpload(DateTimeOffset PublishAt, DateTimeOffset NextAttemptAt, Guid? EditPlanId);

public sealed record YouTubeScheduleSettingsDto(IReadOnlyList<string> Slots, string TimeZoneId, string TimeZoneName,
    int UploadWindowHours);
public sealed record PhoneNotificationSettingsDto(bool Enabled, string Server, string Topic, string SubscribeUrl);
public sealed record UpdatePhoneNotificationsRequest(bool Enabled, string? Server, string? Topic);
public sealed record ScheduleYouTubeRequest(string OutputRelativePath, Guid? EditPlanId, string Title, string Caption,
    bool Confirmed = false, string? IdempotencyKey = null);
public sealed record UnscheduleYouTubeRequest(bool Confirmed = false);
public sealed record YouTubeScheduleDto(YouTubeScheduleSettingsDto Settings, PhoneNotificationSettingsDto Notifications,
    bool Connected, bool CanManage, string? Channel, string Message, DateTimeOffset NextFreeSlot,
    IReadOnlyList<PublishJobDto> Items);

public static class PublishingMappings
{
    public static PublishJobDto ToDto(this PublishJob value) => new(value.Id, value.CampaignId, value.Platform,
        value.Status, value.Progress, value.OutputRelativePath, value.FileName, value.Title, value.Caption,
        value.Privacy, value.ShareToFeed, value.DryRun, value.AccountIdentifier, value.FileSha256,
        value.ExternalId, value.ExternalUrl, value.Error,
        value.ManualPackageRelativePath is null ? null : $"/api/publishing/jobs/{value.Id}/manual-package",
        value.CreatedAt, value.UpdatedAt, value.PublishedAt, value.EditPlanId, value.ScheduledPublishAt,
        value.Status == PublishStatus.Queued ? value.NextAttemptAt : null, value.NotificationScheduledFor is not null);
}
