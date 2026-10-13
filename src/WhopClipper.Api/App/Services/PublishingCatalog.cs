using System.Text;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public static class PlatformPublishingCatalog
{
    public static readonly PlatformKind[] Platforms =
        [PlatformKind.TikTok, PlatformKind.InstagramReels, PlatformKind.YouTubeShorts];

    public static bool IsPublishingPlatform(PlatformKind platform) => Platforms.Contains(platform);
    public static PublishingConnectionMode Mode(PlatformKind platform) =>
        platform == PlatformKind.TikTok ? PublishingConnectionMode.Manual : PublishingConnectionMode.Api;

    public static PlatformPublishingGuideDto Guide(PlatformKind platform) => platform switch
    {
        PlatformKind.TikTok => new(platform, "TikTok", PublishingConnectionMode.Manual,
            "https://www.tiktok.com/signup", "A standard TikTok account. Publishing stays manual in this personal tool.",
            [],
            ["Use a vertical 9:16 video.", "Review the campaign duration, hashtags, mentions and disclosure before posting.", "Keep important text away from interface overlays."],
            ["Download the manual package.", "Open TikTok and choose Upload.", "Select the packaged video, paste caption.txt, review cover and disclosures, then publish manually."],
            ["This app deliberately does not store a TikTok password or automate TikTok login/upload."]),
        PlatformKind.InstagramReels => new(platform, "Instagram Reels", PublishingConnectionMode.Api,
            "https://www.instagram.com/accounts/emailsignup/", "A Professional Instagram account linked to a Facebook Page and an app using Facebook Login for Business.",
            [new("accessToken", "Facebook Page access token", true, true, "Facebook Login for Business token with instagram_basic, instagram_content_publish, pages_show_list, and pages_read_engagement."),
             new("accountId", "Instagram professional account ID", false, true, "The IG user ID used by the Instagram Graph API."),
             new("pageId", "Linked Facebook Page ID", false, true, "Page linked to the Professional Instagram account."),
             new("authMode", "Authentication mode", false, true, "Must be FacebookLoginForBusiness for local resumable binary upload.")],
            ["Vertical 9:16 MP4 is recommended.", "The local resumable upload accepts the rendered campaign output only.", "Caption and campaign disclosures are sent with the Reel."],
            ["Configure the professional account ID and access token.", "Select an output, confirm the caption and click Upload.", "The app uploads bytes locally, waits for the container, then calls media_publish."],
            ["Personal/consumer Instagram accounts cannot use API publishing.", "Instagram Login alone does not enable the local resumable binary flow; this app requires Facebook Login for Business and the linked Page prerequisites."]),
        PlatformKind.YouTubeShorts => new(platform, "YouTube Shorts", PublishingConnectionMode.Api,
            "https://www.youtube.com/create_channel", "A YouTube channel and a Google Cloud OAuth Desktop client with YouTube Data API enabled.",
            [new("clientId", "OAuth client ID", false, true, "Desktop-app OAuth client ID from Google Cloud."),
             new("clientSecret", "OAuth client secret", true, false, "Optional for a Google Desktop OAuth client; stored in the current-user vault when supplied."),
             new("accountId", "YouTube channel handle or ID", false, true, "The exact channel that this browser authorization will upload to; shown again before every upload."),
             new("refreshToken", "OAuth refresh token", true, false, "Normally created by Connect YouTube; paste only for recovery."),
             new("accessToken", "OAuth access token", true, false, "Optional short-lived token; a refresh token is preferred.")],
            ["Use a title no longer than 100 characters.", "Private is the safe default.", "Connecting asks for two permissions: uploading videos, and managing them so a scheduled clip can be moved or taken off."],
            ["Save the Desktop OAuth client ID and exact destination channel; the client secret is optional.", "Choose Connect YouTube and allow both permissions in your browser.", "Select an output, re-check the displayed channel, confirm metadata and upload."],
            ["YouTube service accounts are not supported.", "Unverified API projects may be restricted to private uploads until Google completes an audit."]),
        _ => throw new ArgumentOutOfRangeException(nameof(platform), "Publishing is only available for TikTok, Instagram Reels, and YouTube Shorts.")
    };

    public static IReadOnlyDictionary<string, AccountKitFieldDefinition> AccountFields(PlatformKind platform)
    {
        if (!IsPublishingPlatform(platform)) throw new ArgumentOutOfRangeException(nameof(platform));
        var platformName = Guide(platform).DisplayName;
        return new Dictionary<string, AccountKitFieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = new("Username", false, $"Suggested username for your independent {platformName} highlights channel; do not reuse the featured creator's official username."),
            ["handle"] = new("Handle", true, "A distinct highlights-channel handle. Copy without the @ sign unless the platform asks for it."),
            ["displayName"] = new("Channel display name", true, "Your editable highlights-channel identity, not the featured creator's official account name."),
            ["loginEmail"] = new("Signup email", false, "Non-secret email used during account creation. Passwords are never stored."),
            ["loginPhone"] = new("Signup phone", false, "Optional phone used during account creation. Verification codes are never stored."),
            ["bio"] = new("Bio / description", false, "State the channel's curation value, credit the featured subject, and make clear that this is not the official account."),
            ["website"] = new("Website / link", false, "Public link to paste into the profile when the account is eligible."),
            ["category"] = new("Category", false, platform == PlatformKind.InstagramReels ? "Choose a Professional account category." : "Suggested public category."),
            ["contactEmail"] = new("Public contact email", false, "Optional public-facing contact email; this can differ from the signup email."),
            ["profileImage"] = new("Profile image path or URL", false, "Use original highlights-channel artwork that is visually distinct from the creator's official avatar or logo. The app does not upload it automatically.")
        };
    }

    public static string[] SetupSteps(PlatformKind platform) =>
    [
        $"Open {Guide(platform).SignupUrl} and create the account manually.",
        "Treat the campaign brand as the featured subject, not as the owner or identity of your account.",
        "Copy the prepared fields below into the platform's signup and profile forms.",
        "Keep a distinct highlights-channel name and a clear 'not the official account' disclosure in the bio.",
        "Create a strong unique password in your password manager; do not save it in Whop Clipper.",
        "Finish email/phone verification and enable two-factor authentication directly on the platform.",
        platform == PlatformKind.InstagramReels
            ? "Switch the Instagram account to Professional (Business or Creator) before configuring API publishing."
            : platform == PlatformKind.YouTubeShorts
                ? "Create the channel, then connect it under Connections. The steps are listed there."
                : "Use the manual package for every TikTok upload."
    ];

    public sealed record AccountKitFieldDefinition(string Label, bool Required, string HelpText);
}

public sealed class PlatformAccountKitService(AppDbContext db)
{
    private static readonly string[] BannedFragments =
        ["password", "passcode", "twofactor", "2fa", "otp", "recovery", "backupcode", "securityanswer"];
    private static readonly HashSet<string> LegacyGeneratedIdentityKeys =
        new(["username", "handle", "displayName", "bio"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] GeneratedIdentityKeys = ["username", "handle", "displayName", "bio"];

    public async Task<IReadOnlyList<PlatformAccountKitDto>?> GetAllAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().Include(x => x.PlatformProfiles).SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken);
        if (campaign is null) return null;
        var stored = await db.PlatformAccountKits.AsNoTracking().Where(x => x.CampaignId == campaignId)
            .ToDictionaryAsync(x => x.Platform, cancellationToken);
        return PlatformPublishingCatalog.Platforms.Select(platform => Build(campaign, platform,
            stored.TryGetValue(platform, out var kit) ? ReadFields(kit.FieldsJson) : null)).ToList();
    }

    public async Task<PlatformAccountKitDto?> GetAsync(Guid campaignId, PlatformKind platform, CancellationToken cancellationToken)
    {
        if (!PlatformPublishingCatalog.IsPublishingPlatform(platform)) return null;
        var campaign = await db.Campaigns.AsNoTracking().Include(x => x.PlatformProfiles).SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken);
        if (campaign is null) return null;
        var stored = await db.PlatformAccountKits.AsNoTracking().SingleOrDefaultAsync(
            x => x.CampaignId == campaignId && x.Platform == platform, cancellationToken);
        return Build(campaign, platform, stored is null ? null : ReadFields(stored.FieldsJson));
    }

    public async Task<(PlatformAccountKitDto? Kit, Dictionary<string, string[]>? Errors)> SaveAsync(Guid campaignId,
        PlatformKind platform, IReadOnlyDictionary<string, string>? fields, CancellationToken cancellationToken)
    {
        if (!PlatformPublishingCatalog.IsPublishingPlatform(platform))
            return (null, new() { ["platform"] = ["Choose TikTok, InstagramReels, or YouTubeShorts."] });
        var campaign = await db.Campaigns.Include(x => x.PlatformProfiles)
            .SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken);
        if (campaign is null) return (null, null);
        var errors = Validate(fields, platform);
        if (errors.Count > 0) return (null, errors);
        var clean = (fields ?? new Dictionary<string, string>()).ToDictionary(x => x.Key,
            x => (x.Value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
        var entity = await db.PlatformAccountKits.SingleOrDefaultAsync(
            x => x.CampaignId == campaignId && x.Platform == platform, cancellationToken);
        if (entity is null)
        {
            entity = new PlatformAccountKit { CampaignId = campaignId, Platform = platform };
            db.PlatformAccountKits.Add(entity);
        }
        entity.FieldsJson = System.Text.Json.JsonSerializer.Serialize(clean, JsonDefaults.Serializer);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (Build(campaign, platform, clean), null);
    }

    public async Task<PlatformAccountKitDto?> RegenerateIdentityAsync(Guid campaignId, PlatformKind platform,
        CancellationToken cancellationToken)
    {
        if (!PlatformPublishingCatalog.IsPublishingPlatform(platform)) return null;
        var campaign = await db.Campaigns.Include(x => x.PlatformProfiles)
            .SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken);
        if (campaign is null) return null;
        var entity = await db.PlatformAccountKits.SingleOrDefaultAsync(
            x => x.CampaignId == campaignId && x.Platform == platform, cancellationToken);
        var fields = entity is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadFields(entity.FieldsJson);
        var generated = DefaultFields(campaign, platform);
        foreach (var key in GeneratedIdentityKeys) fields[key] = generated[key];
        if (entity is null)
        {
            entity = new PlatformAccountKit { CampaignId = campaignId, Platform = platform };
            db.PlatformAccountKits.Add(entity);
        }
        entity.FieldsJson = System.Text.Json.JsonSerializer.Serialize(fields, JsonDefaults.Serializer);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Build(campaign, platform, fields);
    }

    internal static Dictionary<string, string[]> Validate(IReadOnlyDictionary<string, string>? fields, PlatformKind platform)
    {
        var errors = new Dictionary<string, string[]>();
        var allowed = PlatformPublishingCatalog.AccountFields(platform);
        foreach (var pair in fields ?? new Dictionary<string, string>())
        {
            var normalized = new string(pair.Key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (BannedFragments.Any(normalized.Contains))
            {
                errors[$"fields.{pair.Key}"] = ["Passwords, verification codes, recovery codes, and security answers must stay in the platform or your password manager."];
                continue;
            }
            if (!allowed.ContainsKey(pair.Key)) errors[$"fields.{pair.Key}"] = ["This account-kit field is not supported."];
            else if ((pair.Value?.Length ?? 0) > 2_000) errors[$"fields.{pair.Key}"] = ["Value must be 2,000 characters or fewer."];
            else if ((pair.Value ?? string.Empty).Any(char.IsControl)) errors[$"fields.{pair.Key}"] = ["Control characters are not allowed."];
        }
        return errors;
    }

    internal static PlatformAccountKitDto Build(Campaign campaign, PlatformKind platform,
        IReadOnlyDictionary<string, string>? stored)
    {
        var defaults = MergeStoredFields(campaign, platform, stored);
        var definitions = PlatformPublishingCatalog.AccountFields(platform);
        var fields = definitions.Select(x => new AccountKitFieldDto(x.Key, x.Value.Label,
            defaults.GetValueOrDefault(x.Key, string.Empty), x.Value.Required, true, x.Value.HelpText)).ToList();
        var guide = PlatformPublishingCatalog.Guide(platform);
        var profile = campaign.PlatformProfiles.SingleOrDefault(x => x.Platform == platform);
        var hashtags = profile is null ? [] : JsonDefaults.Parse(profile.RequiredHashtagsJson, Array.Empty<string>());
        var mentions = profile is null ? [] : JsonDefaults.Parse(profile.RequiredMentionsJson, Array.Empty<string>());
        var captionParts = new[] { profile?.CaptionTemplate, string.Join(' ', hashtags), string.Join(' ', mentions),
            profile?.DisclosureText, profile?.CallToAction }.Where(x => !string.IsNullOrWhiteSpace(x));
        var publishDefaults = new PlatformPublishDefaultsDto(profile?.Enabled ?? true, campaign.Name,
            string.Join(Environment.NewLine, captionParts), hashtags, mentions, profile?.DisclosureText,
            profile?.CallToAction, platform == PlatformKind.InstagramReels ? "public" : "private");
        return new(campaign.Id, platform, guide.DisplayName, guide.ConnectionMode, guide.SignupUrl, fields,
            PlatformPublishingCatalog.SetupSteps(platform), publishDefaults);
    }

    internal static Dictionary<string, string> DefaultFields(Campaign campaign, PlatformKind platform)
    {
        var subject = FeaturedSubject(campaign);
        var display = $"Clip Notes: {subject}";
        var handle = HighlightHandle(subject, platform == PlatformKind.TikTok ? 24 : 30);
        var category = string.IsNullOrWhiteSpace(campaign.Category) ? "Digital creator" : campaign.Category.Trim();
        var bio = $"Independent highlights featuring {subject}. Curated edits with captions and added context. Not the creator's official account.";
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = handle, ["handle"] = handle, ["displayName"] = display,
            ["loginEmail"] = "", ["loginPhone"] = "", ["bio"] = bio,
            ["website"] = campaign.WhopUrl ?? "", ["category"] = category,
            ["contactEmail"] = "", ["profileImage"] = ""
        };
    }

    internal static Dictionary<string, string> MergeStoredFields(Campaign campaign, PlatformKind platform,
        IReadOnlyDictionary<string, string>? stored)
    {
        var defaults = DefaultFields(campaign, platform);
        if (stored is null) return defaults;
        var legacy = LegacyGeneratedIdentityFields(campaign, platform);
        foreach (var pair in stored)
        {
            if (!defaults.ContainsKey(pair.Key)) continue;
            var isLegacyGenerated = LegacyGeneratedIdentityKeys.Contains(pair.Key) &&
                                    legacy.TryGetValue(pair.Key, out var oldValue) &&
                                    string.Equals(pair.Value, oldValue, StringComparison.Ordinal);
            if (!isLegacyGenerated) defaults[pair.Key] = pair.Value;
        }
        return defaults;
    }

    private static Dictionary<string, string> LegacyGeneratedIdentityFields(Campaign campaign, PlatformKind platform)
    {
        var subject = FeaturedSubject(campaign);
        var handle = Slug(subject, platform == PlatformKind.TikTok ? 24 : 30);
        var bio = platform switch
        {
            PlatformKind.YouTubeShorts => $"Short-form highlights from {subject}.",
            PlatformKind.InstagramReels => $"Clips, highlights and updates from {subject}.",
            _ => $"The best moments from {subject}."
        };
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = handle,
            ["handle"] = handle,
            ["displayName"] = subject,
            ["bio"] = bio
        };
    }

    private static string FeaturedSubject(Campaign campaign)
    {
        var subject = string.IsNullOrWhiteSpace(campaign.Brand) ? campaign.Name : campaign.Brand;
        return string.IsNullOrWhiteSpace(subject) ? "Featured Creator" : subject.Trim();
    }

    private static string HighlightHandle(string subject, int maxLength)
    {
        const string identity = "clipnotes";
        var subjectSlug = Slug(subject, maxLength);
        var remaining = Math.Max(3, maxLength - identity.Length);
        var subjectPart = subjectSlug[..Math.Min(subjectSlug.Length, remaining)].Trim('.', '_', '-');
        if (string.IsNullOrWhiteSpace(subjectPart)) subjectPart = "highlights";
        var result = $"{identity}{subjectPart}";
        return result[..Math.Min(result.Length, maxLength)];
    }

    private static string Slug(string value, int maxLength)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-') builder.Append(character);
        var result = builder.ToString().Trim('.', '_');
        if (string.IsNullOrWhiteSpace(result)) result = "creatorclips";
        if (result.Length < 3) result = $"{result}clips";
        return result[..Math.Min(result.Length, maxLength)];
    }

    private static Dictionary<string, string> ReadFields(string json) =>
        JsonDefaults.Parse(json, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
