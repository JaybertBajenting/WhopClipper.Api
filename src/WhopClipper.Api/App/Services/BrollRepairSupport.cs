using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

public sealed record BrollRepairSelection(double StartSeconds, double EndSeconds, int FirstShot, int LastShotExclusive)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public sealed record BrollRepairCandidate(Guid PlanId, Guid RepairId, double StartSeconds, double EndSeconds,
    string SpokenText, string OriginalPlanHash, string OriginalVideoHash, IReadOnlyList<BrollShotDto> ReplacementShots,
    string CandidateRelativePath, string? CandidatePreviewRelativePath, DateTimeOffset CreatedAt);

/// <summary>Only whole lower-panel shots are exchanged. The soundtrack, speech cut, subtitle clock and
/// top panel are never retimed. A mark in the middle of a shot expands to that shot's boundaries.</summary>
public static class BrollRepairSupport
{
    /// <summary>An AI repair plans footage for at most this much of a clip: longer marks got vaguer plans.</summary>
    public const double AiMaxSeconds = 20;

    /// <summary>Footage picked by hand in the B-roll editor may replace any run of shots, the whole clip included.</summary>
    public const double EditorMaxSeconds = 600;

    public static BrollRepairSelection Select(IReadOnlyList<BrollShotDto> shots, double from, double to,
        double maxSeconds = AiMaxSeconds)
    {
        var duration = shots.Sum(x => x.DurationSeconds);
        if (shots.Count == 0 || !double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to <= from ||
            to > duration + 0.02 || to - from > maxSeconds)
            throw new ArgumentOutOfRangeException(nameof(from), $"Mark a valid B-roll interval of at most {maxSeconds:0} seconds.");
        var clock = 0d;
        var first = -1;
        var last = -1;
        var start = 0d;
        var end = 0d;
        // Shot times reach the editor rounded to the millisecond. A 0.0001 s tolerance read a boundary sent as 2.232
        // for a shot starting at 2.2325 as touching the shot before, and pulled it into every edit. 2 ms is still far
        // under one frame.
        const double tolerance = 0.002;
        for (var index = 0; index < shots.Count; index++)
        {
            var next = clock + shots[index].DurationSeconds;
            if (next > from + tolerance && clock < to - tolerance)
            {
                if (first < 0) { first = index; start = clock; }
                last = index + 1;
                end = next;
            }
            clock = next;
        }
        if (first < 0 || end - start > maxSeconds + 5) throw new ArgumentOutOfRangeException(nameof(from), "No repairable shots intersect this mark.");
        return new(Math.Round(start, 3), Math.Round(end, 3), first, last);
    }

    public static IReadOnlyList<BrollShotDto> Replace(IReadOnlyList<BrollShotDto> original,
        BrollRepairSelection selection, IReadOnlyList<BrollShotDto> replacement)
    {
        if (replacement.Count == 0 || Math.Abs(replacement.Sum(x => x.DurationSeconds) - selection.DurationSeconds) > 0.02)
            throw new ArgumentException("Replacement footage must cover the selected shots exactly.", nameof(replacement));
        return [.. original.Take(selection.FirstShot), .. replacement, .. original.Skip(selection.LastShotExclusive)];
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Sha256File(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    /// <summary>Replace the lower panel for only the marked window. The original MP4 is input zero;
    /// its audio stream is copied, not decoded or mixed. A transparent ASS layer redraws only the
    /// lower half of existing word captions, because those letters cross the panel seam.</summary>
    public static IReadOnlyList<string> BuildFfmpegArguments(string original, string output, string? captions,
        IReadOnlyList<BrollShotDto> shots, double start, double end, double total, int crf = 20)
    {
        if (shots.Count == 0 || start < 0 || end <= start || total < end ||
            Math.Abs(shots.Sum(x => x.DurationSeconds) - (end - start)) > 0.02)
            throw new ArgumentException("Replacement shots must fill the marked window.", nameof(shots));
        static string N(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
        static string Escape(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-threads", "1", "-filter_complex_threads", "1", "-i", original };
        var graph = new List<string>();
        var labels = new StringBuilder();
        for (var index = 0; index < shots.Count; index++)
        {
            var shot = shots[index];
            if (!File.Exists(shot.RelativePath)) throw new FileNotFoundException("Approved B-roll footage is missing.", shot.RelativePath);
            args.AddRange(["-ss", N(shot.InSeconds), "-t", N(shot.DurationSeconds + 0.5), "-i", shot.RelativePath]);
            graph.Add($"[{index + 1}:v]crop={shot.Crop.Width}:{shot.Crop.Height}:{shot.Crop.X}:{shot.Crop.Y}," +
                      $"scale=1080:960,setsar=1,fps=30,tpad=stop_mode=clone:stop_duration={N(shot.DurationSeconds)}," +
                      $"trim=duration={N(shot.DurationSeconds)},setpts=PTS-STARTPTS[b{index}]");
            labels.Append($"[b{index}]");
        }
        graph.Add($"{labels}concat=n={shots.Count}:v=1:a=0,setpts=PTS-STARTPTS+{N(start)}/TB[panel]");
        var enable = $"gte(t,{N(start)})*lt(t,{N(end)})";
        graph.Add($"[0:v][panel]overlay=0:960:enable='{enable}':eof_action=pass:repeatlast=0[patched]");
        if (captions is not null)
        {
            graph.Add($"color=c=black@0.0:s=1080x1920:r=30:d={N(total)},format=rgba," +
                      $"subtitles='{Escape(captions)}':alpha=1,crop=1080:960:0:960,format=rgba[captionlower]");
            graph.Add($"[patched][captionlower]overlay=0:960:enable='{enable}':eof_action=pass:repeatlast=0," +
                      "format=yuv420p,setsar=1[vout]");
        }
        else graph.Add("[patched]format=yuv420p,setsar=1[vout]");
        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]", "-map", "0:a:0", "-t", N(total),
            "-c:v", "libx264", "-preset", "fast", "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-threads", "2", "-c:a", "copy", "-movflags", "+faststart", output]);
        return args;
    }
}
