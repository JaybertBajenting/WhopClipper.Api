using System.Text;
using System.Text.RegularExpressions;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed record ClipPublishCopy(string Title, string Caption);

/// <summary>
/// Builds the title and caption that go with a finished clip, so uploading it by hand is copy and
/// paste rather than remembering what each one was about and which hashtag the campaign demands.
///
/// A missed required hashtag is not cosmetic: campaigns reject clips for it, and the clip has
/// already been rendered by the time anyone is filling in an upload form.
/// </summary>
public static partial class ClipCaptionComposer
{
    /// <summary>Longest a title stays useful as a one-line label; the caption carries the rest.</summary>
    private const int MaxTitleLength = 100;

    public static ClipPublishCopy Compose(EditPlan? plan, PlatformProfile? profile, string fileName)
    {
        var title = TitleFor(plan, fileName);
        var builder = new StringBuilder(title);

        var template = profile?.CaptionTemplate?.Trim();
        if (!string.IsNullOrEmpty(template)) builder.AppendLine().AppendLine().Append(template);

        var extras = plan is null ? null : JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
        var cta = Coalesce(extras?.CtaOverlayText, profile?.CallToAction);
        if (cta is not null) builder.AppendLine().AppendLine().Append(cta);

        // Disclosure before the tags, because a disclosure buried under a wall of hashtags is the
        // kind of thing a brand safety review calls "not clearly disclosed".
        if (Coalesce(profile?.DisclosureText) is { } disclosure)
            builder.AppendLine().AppendLine().Append(disclosure);

        var mentions = profile is null ? [] : JsonDefaults.Parse(profile.RequiredMentionsJson, Array.Empty<string>());
        var tags = mentions.Select(x => x.Trim()).Where(x => x.Length > 0)
            .Concat(HashtagsFor(plan, profile, title)).ToList();
        if (tags.Count > 0) builder.AppendLine().AppendLine().Append(string.Join(' ', tags));

        return new ClipPublishCopy(title, builder.ToString().Trim());
    }

    /// <summary>Total tags on one post. Past a dozen it reads as spam and platforms discount them.</summary>
    private const int MaxHashtags = 12;

    /// <summary>
    /// The campaign's required tags first - those are a payout condition - then the campaign's own
    /// set, then the platform's, then whatever the clip itself is about.
    ///
    /// A brief usually names one mandatory hashtag, which is a floor and not a ceiling: posts that
    /// actually travel carry the subject, the niche and the platform tag as well. The rest are
    /// campaign-wide and belong in the platform profile; only the last group can be per-clip.
    /// </summary>
    internal static IReadOnlyList<string> HashtagsFor(EditPlan? plan, PlatformProfile? profile, string title)
    {
        // (tag, lower-case it) - only the ones this code invents get re-cased.
        var ordered = new List<(string Raw, bool Lowercase)>();
        if (profile is not null)
            ordered.AddRange(JsonDefaults.Parse(profile.RequiredHashtagsJson, Array.Empty<string>())
                .Select(x => (x, false)));
        if (plan is not null) ordered.Add((PlatformTag(plan.Platform), true));
        ordered.AddRange(SubjectTags(title).Select(x => (x, true)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var (raw, lowercase) in ordered)
        {
            var tag = Normalize(raw, lowercase);
            if (tag is null || !seen.Add(tag)) continue;
            result.Add(tag);
            if (result.Count == MaxHashtags) break;
        }
        return result;
    }

    /// <summary>Where the clip is going. Omitting it is the cheapest reach a short-form post gives away.</summary>
    private static string PlatformTag(PlatformKind platform) => platform switch
    {
        PlatformKind.YouTubeShorts => "#shorts",
        PlatformKind.InstagramReels => "#reels",
        PlatformKind.TikTok => "#fyp",
        _ => string.Empty,
    };

    /// <summary>
    /// Who and what the clip is about, taken from names in the hook. Deliberately only proper nouns:
    /// pulling ordinary words out of a hook produces tags nobody would post - measured on a real
    /// batch it offered "#butt" and "#thong" - whereas names give "#clix" and "#markwahlberg",
    /// which are exactly the tags competing clips on this campaign carry.
    /// </summary>
    internal static IReadOnlyList<string> SubjectTags(string title)
    {
        var tags = new List<string>();
        foreach (Match match in ProperNounRegex().Matches(title))
        {
            var words = match.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A capital only proves a name when it is not simply starting the sentence, so a
            // single leading word is dropped - otherwise "Why Lacy said no" yields "#whylacy".
            if (words.Length > 1 && SentenceOpeners.Contains(words[0]))
                words = words[1..];
            if (words.Length == 1 && SentenceOpeners.Contains(words[0])) continue;
            tags.Add(string.Concat(words));
        }
        tags.AddRange(AcronymRegex().Matches(title).Select(x => x.Value));
        return tags.Where(x => x.Length is >= 3 and <= 24).ToList();
    }

    private static readonly HashSet<string> SentenceOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "We", "You", "He", "She", "It", "They", "My", "The", "A", "An", "Why", "How", "What", "When",
        "Made", "This", "That", "I", "His", "Her", "Their", "Our", "And", "But", "So", "Then", "Now",
        "No", "Yes", "If", "Who", "Where", "One", "Two", "Every", "Never", "Always",
    };

    /// <summary>
    /// Stripped to what a platform accepts in a tag, keeping the casing it was written with.
    ///
    /// Briefs name their tag with deliberate capitals - "#DoubleCoverage" - and a human reviews the
    /// post against the brief. Tags match case-insensitively everywhere, so lower-casing would still
    /// work, but it would no longer look like the thing that was asked for. Names lifted out of a
    /// hook are lower-cased instead, which is the convention for those.
    /// </summary>
    private static string? Normalize(string? raw, bool lowercase)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = TagCharacterRegex().Replace(raw.Trim().TrimStart('#'), string.Empty);
        if (lowercase) cleaned = cleaned.ToLowerInvariant();
        return cleaned.Length == 0 ? null : "#" + cleaned;
    }

    /// <summary>
    /// The hook burned onto the clip, which is what the clip is actually about. The output filename
    /// is the fallback and needs unpicking first - it is a slug with the render's job id glued on.
    /// </summary>
    internal static string TitleFor(EditPlan? plan, string fileName)
    {
        var extras = plan is null ? null : JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null);
        var hook = extras?.Overlays?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Text))?.Text;
        if (!string.IsNullOrWhiteSpace(hook)) return Trim(hook.Trim().Trim('"', '“', '”'));

        var stem = Path.GetFileNameWithoutExtension(plan?.OutputFileName ?? fileName);
        // "<slug>-<32 hex job id>" - drop the id, then make the slug readable again.
        stem = JobSuffixRegex().Replace(stem, string.Empty).Replace('_', ' ').Replace('-', ' ');
        stem = WhitespaceRegex().Replace(stem, " ").Trim();
        return Trim(stem.Length == 0 ? Path.GetFileNameWithoutExtension(fileName) : stem);
    }

    private static string Trim(string value) =>
        value.Length <= MaxTitleLength ? value : value[..MaxTitleLength].TrimEnd() + "…";

    private static string? Coalesce(params string?[] values) =>
        values.Select(x => x?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));

    [GeneratedRegex(@"-[0-9a-f]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex JobSuffixRegex();
    /// <summary>Runs of capitalised words - a person, a team, a game.</summary>
    [GeneratedRegex(@"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b")]
    private static partial Regex ProperNounRegex();
    /// <summary>ACL, NBA, GTA - short all-caps tokens read as names, not shouting.</summary>
    [GeneratedRegex(@"\b[A-Z]{2,5}\b")]
    private static partial Regex AcronymRegex();
    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex TagCharacterRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
