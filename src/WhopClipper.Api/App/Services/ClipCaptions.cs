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
        var hashtags = profile is null ? [] : JsonDefaults.Parse(profile.RequiredHashtagsJson, Array.Empty<string>());
        var tags = mentions.Concat(hashtags).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (tags.Count > 0) builder.AppendLine().AppendLine().Append(string.Join(' ', tags));

        return new ClipPublishCopy(title, builder.ToString().Trim());
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
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
