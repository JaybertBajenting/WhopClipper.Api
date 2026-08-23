using System.Text.RegularExpressions;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed record DetectedLink(string Url, string Host, string Kind, string Context, int Occurrences,
    bool AlreadySource, bool AlreadyReference);

/// <summary>
/// Pulls URLs out of a campaign brief so source and reference media do not have to be
/// retyped. Detection only proposes candidates; downloading still needs the user to
/// confirm they are allowed to use the media, because brief text is untrusted input.
/// </summary>
public sealed partial class BriefLinkDetector
{
    private const int MaxContextCharacters = 180;
    private const int MaxLinks = 100;

    /// <summary>Hosts that usually carry the campaign's actual footage.</summary>
    private static readonly string[] MediaHosts =
    [
        "youtube.com", "youtu.be", "tiktok.com", "instagram.com", "twitch.tv", "vimeo.com",
        "streamable.com", "kick.com", "rumble.com", "dailymotion.com", "facebook.com", "fb.watch",
        "drive.google.com", "dropbox.com", "1drv.ms", "onedrive.live.com", "mega.nz", "we.tl", "wetransfer.com",
    ];

    /// <summary>Hosts that are campaign admin rather than media, so they are never proposed as footage.</summary>
    private static readonly string[] AdminHosts = ["whop.com", "discord.gg", "discord.com"];

    public IReadOnlyList<DetectedLink> Detect(string text, IEnumerable<string> existingSources,
        IEnumerable<string> existingReferences)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var sources = new HashSet<string>(existingSources.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<string>(existingReferences.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, (string Url, string Context, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UrlRegex().Matches(text))
        {
            var url = TrimTrailingPunctuation(match.Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var key = Normalize(url);
            if (found.TryGetValue(key, out var existing))
            {
                found[key] = (existing.Url, existing.Context, existing.Count + 1);
                continue;
            }
            if (found.Count >= MaxLinks) break;
            found[key] = (url, ContextFor(text, match.Index), 1);
        }

        return found.Values
            .Select(entry =>
            {
                var host = Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ? uri.Host.TrimStart("www.".ToCharArray()) : string.Empty;
                var normalized = Normalize(entry.Url);
                return new DetectedLink(entry.Url, HostOf(entry.Url), Classify(entry.Url), entry.Context, entry.Count,
                    sources.Contains(normalized), references.Contains(normalized));
            })
            .OrderByDescending(x => x.Kind == "Media")
            .ThenByDescending(x => x.Occurrences)
            .ToList();
    }

    internal static string Classify(string url)
    {
        var host = HostOf(url);
        if (AdminHosts.Any(known => host.Equals(known, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + known, StringComparison.OrdinalIgnoreCase)))
            return "Admin";
        if (MediaHosts.Any(known => host.Equals(known, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + known, StringComparison.OrdinalIgnoreCase)))
            return "Media";
        // x.com and twitter.com only carry media on a status permalink.
        if ((host.EndsWith("x.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("twitter.com", StringComparison.OrdinalIgnoreCase))
            && url.Contains("/status/", StringComparison.OrdinalIgnoreCase))
            return "Media";
        return "Other";
    }

    internal static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host)
            : string.Empty;

    /// <summary>Ignores casing, a trailing slash, and tracking noise so a repeat link is not offered twice.</summary>
    internal static string Normalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().TrimEnd('/');
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');
        var query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                && !part.StartsWith("si=", StringComparison.OrdinalIgnoreCase)
                && !part.StartsWith("feature=", StringComparison.OrdinalIgnoreCase)));
        return $"{host}{path}{(query.Length > 0 ? "?" + query : string.Empty)}".ToLowerInvariant();
    }

    private static string TrimTrailingPunctuation(string url)
    {
        var trimmed = url.TrimEnd('.', ',', ';', ':', '!', '?', '"', '\'');
        // A closing bracket only belongs to the URL when it was opened inside it.
        while (trimmed.Length > 0 && trimmed[^1] is ')' or ']' or '>'
            && trimmed.Count(c => c == '(') < trimmed.Count(c => c == ')'))
            trimmed = trimmed[..^1];
        return trimmed;
    }

    private static string ContextFor(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        var line = text[start..end].Trim();
        return line.Length <= MaxContextCharacters ? line : line[..MaxContextCharacters] + "…";
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}

/// <summary>Browsers yt-dlp can read cookies from. Keeping this closed stops an arbitrary
/// string from reaching the yt-dlp command line.</summary>
public static class YtDlpCookieSources
{
    public static readonly string[] Browsers =
        ["brave", "chrome", "chromium", "edge", "firefox", "opera", "safari", "vivaldi", "whale"];

    public static bool IsSupportedBrowser(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Browsers.Contains(value.Trim().ToLowerInvariant());

    /// <summary>JavaScript runtimes yt-dlp accepts, most preferred first.</summary>
    public static readonly string[] JsRuntimes = ["node", "deno", "bun", "quickjs"];

    public static bool IsSupportedJsRuntime(string? value) =>
        !string.IsNullOrWhiteSpace(value) && JsRuntimes.Contains(value.Trim().ToLowerInvariant());
}
