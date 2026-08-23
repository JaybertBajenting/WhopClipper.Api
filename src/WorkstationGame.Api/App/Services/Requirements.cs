using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public interface IRequirementExtractor
{
    IReadOnlyList<RequirementItem> Extract(string instructions);
}

public sealed partial class HeuristicRequirementExtractor : IRequirementExtractor
{
    public IReadOnlyList<RequirementItem> Extract(string instructions)
    {
        var items = new List<RequirementItem>();
        if (string.IsNullOrWhiteSpace(instructions))
        {
            items.Add(Item("Review", "missingInstructions", "No instructions supplied", string.Empty, .1, true));
            return items;
        }

        var lines = instructions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AddPlatforms(instructions, items);
        AddDuration(instructions, items);
        AddAspectRatio(instructions, items);
        AddTokens(lines, HashtagRegex(), "Publishing", "hashtag", items);
        AddTokens(lines, MentionRegex(), "Publishing", "mention", items);

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (lower.Contains("subtitle") || lower.Contains("captioned"))
                AddDistinct(items, Item("Editing", "subtitles", lower.Contains("no subtitle") ? "prohibited" : "required", line, .8, false));
            if (lower.Contains("watermark") || lower.Contains("logo"))
                AddDistinct(items, Item("Branding", "branding", line, line, .65, true));
            if (lower.Contains("must not") || lower.Contains("do not") || lower.Contains("prohibited") || lower.Contains("avoid "))
                AddDistinct(items, Item("Prohibited", "restriction", line, line, .8, false));
            if (lower.Contains("call to action") || lower.Contains("cta") || lower.Contains("link in bio"))
                AddDistinct(items, Item("Publishing", "callToAction", line, line, .7, true));
        }

        if (!items.Any(x => x.Key == "platform"))
            items.Add(Item("Platform", "platform", "TikTok", "Application default; no platform found in instructions", .2, true));
        if (!items.Any(x => x.Key == "duration"))
            items.Add(Item("Video", "duration", "15-60 seconds", "Application default; no duration found in instructions", .2, true));
        return items;
    }

    /// <summary>
    /// Detects required platforms with word-boundary matches. Plain substring matching missed
    /// a bare "Youtube" - only "youtube shorts" and "shorts" were listed - and matched " x "
    /// inside unrelated text such as "1920 x 1080".
    /// </summary>
    private static void AddPlatforms(string text, List<RequirementItem> items)
    {
        var platforms = new (Regex Pattern, string Value)[]
        {
            (TikTokRegex(), "TikTok"),
            (InstagramRegex(), "InstagramReels"),
            (YouTubeRegex(), "YouTubeShorts"),
            (XPlatformRegex(), "X")
        };
        foreach (var (pattern, value) in platforms)
        {
            var match = pattern.Match(text);
            if (match.Success) items.Add(Item("Platform", "platform", value, FindSourceLine(text, match.Value), .92, false));
        }
    }

    private static void AddDuration(string text, List<RequirementItem> items)
    {
        var match = DurationRangeRegex().Match(text);
        if (match.Success)
        {
            items.Add(Item("Video", "duration", $"{match.Groups[1].Value}-{match.Groups[2].Value} seconds", FindSourceLine(text, match.Value), .9, false));
            return;
        }
        // A brief that states only a floor - "Minimum 10 seconds" - matched neither pattern and
        // fell through to the application default, which invents a 15s floor stricter than the
        // client actually asked for. A stricter invented rule is worse than none: it rejects
        // clips the client would have accepted.
        var minimum = MinDurationRegex().Match(text);
        var maximum = MaxDurationRegex().Match(text);
        if (minimum.Success && maximum.Success)
        {
            items.Add(Item("Video", "duration", $"{minimum.Groups[1].Value}-{maximum.Groups[1].Value} seconds",
                FindSourceLine(text, minimum.Value), .9, false));
            return;
        }
        if (minimum.Success)
        {
            items.Add(Item("Video", "duration", $"at least {minimum.Groups[1].Value} seconds",
                FindSourceLine(text, minimum.Value), .8, false));
            return;
        }
        if (maximum.Success)
            items.Add(Item("Video", "duration", $"up to {maximum.Groups[1].Value} seconds", FindSourceLine(text, maximum.Value), .8, false));
    }

    private static void AddAspectRatio(string text, List<RequirementItem> items)
    {
        var match = AspectRatioRegex().Match(text);
        if (match.Success)
            items.Add(Item("Video", "aspectRatio", match.Value.Replace(" ", string.Empty), FindSourceLine(text, match.Value), .95, false));
    }

    private static void AddTokens(string[] lines, Regex regex, string category, string key, List<RequirementItem> items)
    {
        foreach (var line in lines)
            foreach (Match match in regex.Matches(line))
                AddDistinct(items, Item(category, key, match.Value, line, .95, false));
    }

    private static void AddDistinct(List<RequirementItem> items, RequirementItem item)
    {
        if (!items.Any(x => x.Key == item.Key && x.Value.Equals(item.Value, StringComparison.OrdinalIgnoreCase))) items.Add(item);
    }

    private static RequirementItem Item(string category, string key, string value, string source, double confidence, bool review) =>
        new() { Category = category, Key = key, Value = value, SourceText = source, Confidence = confidence, NeedsReview = review };

    private static string FindSourceLine(string text, string token) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(x => x.Contains(token, StringComparison.OrdinalIgnoreCase))?.Trim() ?? token;

    [GeneratedRegex(@"\b(\d{1,4})\s*(?:-|–|to)\s*(\d{1,4})\s*(?:seconds?|secs?|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRangeRegex();
    [GeneratedRegex(@"\btik[\s-]?tok\b", RegexOptions.IgnoreCase)]
    private static partial Regex TikTokRegex();
    [GeneratedRegex(@"\b(?:instagram|insta|reels?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstagramRegex();
    [GeneratedRegex(@"\b(?:youtube|you[\s-]tube|yt|shorts?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeRegex();
    // A lone "x" is far too common to match loosely, so only an explicit brand reference counts.
    [GeneratedRegex(@"\btwitter\b|\bx\.com\b|(?<![\w#@])X(?=\s*(?:,|/|&|\)|\.|$)|\s+(?:and|or)\b)")]
    private static partial Regex XPlatformRegex();
    [GeneratedRegex(@"\b(?:minimum|min|at least|no less than|longer than)\s*(?:of\s*)?(\d{1,4})\s*(?:seconds?|secs?|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinDurationRegex();
    [GeneratedRegex(@"(?:maximum|max|under|up to)\s*(?:of\s*)?(\d{1,4})\s*(?:seconds?|secs?|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MaxDurationRegex();
    [GeneratedRegex(@"\b(?:9\s*:\s*16|1\s*:\s*1|16\s*:\s*9|4\s*:\s*5)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AspectRatioRegex();
    [GeneratedRegex(@"(?<!\w)#[\p{L}\p{N}_]+", RegexOptions.IgnoreCase)]
    private static partial Regex HashtagRegex();
    [GeneratedRegex(@"(?<![\w@])@[\p{L}\p{N}_.]+", RegexOptions.IgnoreCase)]
    private static partial Regex MentionRegex();
}

public sealed class RequirementService(AppDbContext db, IRequirementExtractor extractor)
{
    public async Task<CampaignRequirementRevision?> ExtractAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.Include(x => x.RequirementRevisions).Include(x => x.Documents)
            .SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken);
        if (campaign is null) return null;
        // Attached PDF/Word briefs are folded in here so the revision keeps one immutable, auditable source text.
        var instructions = RequirementSourceComposer.Compose(campaign.RawInstructions, campaign.Documents);
        var revision = new CampaignRequirementRevision
        {
            CampaignId = campaignId,
            Version = campaign.RequirementRevisions.Count == 0 ? 1 : campaign.RequirementRevisions.Max(x => x.Version) + 1,
            RawInstructions = instructions,
            Items = extractor.Extract(instructions).ToList()
        };
        db.RequirementRevisions.Add(revision);
        campaign.Status = CampaignStatus.NeedsReview;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return revision;
    }

    public async Task<CampaignRequirementRevision?> ApproveAsync(Guid campaignId, Guid revisionId, CancellationToken cancellationToken)
    {
        var revision = await db.RequirementRevisions.Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == revisionId && x.CampaignId == campaignId, cancellationToken);
        if (revision is null) return null;
        if (!revision.IsApproved)
        {
            var previouslyApproved = await db.RequirementRevisions
                .Where(x => x.CampaignId == campaignId && x.IsApproved && x.Id != revisionId).ToListAsync(cancellationToken);
            foreach (var previous in previouslyApproved) previous.IsApproved = false;
            revision.IsApproved = true;
            revision.ApprovedAt = DateTimeOffset.UtcNow;
            var campaign = await db.Campaigns.SingleAsync(x => x.Id == campaignId, cancellationToken);
            campaign.Status = CampaignStatus.Ready;
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await GenerateProfilesAsync(campaignId, revision.Items, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return revision;
    }

    private async Task GenerateProfilesAsync(Guid campaignId, IEnumerable<RequirementItem> itemSource, CancellationToken cancellationToken)
    {
        var items = itemSource.ToList();
        var parsedPlatforms = items.Where(x => x.Key == "platform")
            .Select(x => Enum.TryParse<PlatformKind>(x.Value, true, out var value) ? value : (PlatformKind?)null)
            .OfType<PlatformKind>().Distinct().ToList();
        if (parsedPlatforms.Count == 0) parsedPlatforms.Add(PlatformKind.TikTok);
        var duration = ParseDuration(items.FirstOrDefault(x => x.Key == "duration")?.Value);
        var hashtags = items.Where(x => x.Key == "hashtag").Select(x => x.Value).ToArray();
        var mentions = items.Where(x => x.Key == "mention").Select(x => x.Value).ToArray();
        var cta = items.FirstOrDefault(x => x.Key == "callToAction")?.Value;
        var subtitleRequired = !items.Any(x => x.Key == "subtitles" && x.Value == "prohibited");
        var existingProfiles = await db.PlatformProfiles.Where(x => x.CampaignId == campaignId).ToListAsync(cancellationToken);
        foreach (var removed in existingProfiles.Where(x => !parsedPlatforms.Contains(x.Platform)))
        {
            removed.Enabled = false;
            removed.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var platform in parsedPlatforms)
        {
            var profile = existingProfiles.SingleOrDefault(x => x.Platform == platform);
            if (profile is null)
            {
                profile = new PlatformProfile { CampaignId = campaignId, Platform = platform };
                db.PlatformProfiles.Add(profile);
            }
            profile.Enabled = true;
            // Approving a later revision must not discard fields the user saved by hand.
            // Re-extracting to pick up an improved parse would otherwise wipe their edits.
            if (IsUserOverridden(profile))
            {
                profile.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }
            profile.MinDurationSeconds = duration.Min;
            profile.PreferredDurationSeconds = duration.Preferred;
            profile.MaxDurationSeconds = duration.Max;
            profile.RequiredHashtagsJson = JsonDefaults.StringArray(hashtags);
            profile.RequiredMentionsJson = JsonDefaults.StringArray(mentions);
            profile.CallToAction = cta;
            profile.BurnSubtitles = subtitleRequired;
            profile.ComplianceChecklistJson = JsonDefaults.StringArray(items.Select(x => x.Value));
            profile.ValueOriginsJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["duration"] = items.Any(x => x.Key == "duration" && x.Confidence > .5) ? "extracted" : "default",
                ["resolution"] = "default", ["hashtags"] = hashtags.Length > 0 ? "extracted" : "default"
            }, JsonDefaults.Serializer);
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsUserOverridden(PlatformProfile profile) =>
        JsonDefaults.Parse(profile.ValueOriginsJson, new Dictionary<string, string>())
            .Any(origin => origin.Value.Equals("userOverride", StringComparison.OrdinalIgnoreCase));

    private static (double Min, double Preferred, double Max) ParseDuration(string? value)
    {
        var numbers = Regex.Matches(value ?? string.Empty, @"\d+").Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length >= 2) return (numbers[0], Math.Min(numbers[1], Math.Max(numbers[0], 30)), numbers[1]);
        if (numbers.Length == 1) return (Math.Min(15, numbers[0]), Math.Min(30, numbers[0]), numbers[0]);
        return (15, 30, 60);
    }
}
