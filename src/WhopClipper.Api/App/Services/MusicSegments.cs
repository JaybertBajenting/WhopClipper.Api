using System.Globalization;
using System.Text.RegularExpressions;

namespace WhopClipper.Api.App.Services;

/// <summary>Where a song should start and stop, in seconds from the top of the file.</summary>
public readonly record struct MusicSegment(double StartSeconds, double EndSeconds);

/// <summary>
/// Reads the segment guide that sits beside a folder of songs. Matthew identified the music the
/// reference channel uses and wrote down which part of each track plays under a short, because the
/// usable hook is often a minute or two in: starting every song at 0:00 would use the intro instead.
///
/// The guide is a markdown list, one "## &lt;file name&gt;" heading per song followed by the shorts it
/// was found in, each with its range:
/// <code>
/// ## Lil Baby - Dead Fresh.mp3
/// Used in:
/// - `What Cris Carter did with his first NFL check [uvj1A3IpykM]` -&gt; use **2:11-2:39** (clip length 28.3s)
/// </code>
/// A song listed against several shorts keeps the first range; they are alternative placements of
/// the same hook, not a sequence.
/// </summary>
public static class MusicSegmentGuide
{
    public const string FileName = "SEGMENTS_TO_USE.md";

    private static readonly Regex Heading = new(@"^##\s+(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Range = new(
        @"use\s*\*\*\s*(?<fromMinutes>\d+):(?<fromSeconds>\d{1,2})\s*-\s*(?<toMinutes>\d+):(?<toSeconds>\d{1,2})\s*\*\*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>File name to segment. Names are compared the way Windows compares them.</summary>
    public static IReadOnlyDictionary<string, MusicSegment> Parse(string markdown)
    {
        var segments = new Dictionary<string, MusicSegment>(StringComparer.OrdinalIgnoreCase);
        string? song = null;
        foreach (var line in markdown.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (Heading.Match(text) is { Success: true } heading)
            {
                song = heading.Groups["name"].Value;
                continue;
            }
            // Only the first range under a heading counts, and a range before any heading has no song.
            if (song is null || segments.ContainsKey(song)) continue;
            if (Range.Match(text) is not { Success: true } range) continue;
            var start = Seconds(range, "fromMinutes", "fromSeconds");
            var end = Seconds(range, "toMinutes", "toSeconds");
            if (end > start) segments[song] = new MusicSegment(start, end);
        }
        return segments;
    }

    /// <summary>The guide for a folder, or an empty map when it has none.</summary>
    public static async Task<IReadOnlyDictionary<string, MusicSegment>> ReadAsync(string folder,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return new Dictionary<string, MusicSegment>();
        return Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static double Seconds(Match match, string minutesGroup, string secondsGroup) =>
        int.Parse(match.Groups[minutesGroup].Value, CultureInfo.InvariantCulture) * 60
        + int.Parse(match.Groups[secondsGroup].Value, CultureInfo.InvariantCulture);
}
