using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

public sealed record ShotStatistics(int CutCount, double CutsPerMinute, double AverageShotSeconds,
    double MedianShotSeconds, double ShortestShotSeconds, double LongestShotSeconds, double OpeningShotSeconds);
public sealed record LoudnessStatistics(double? IntegratedLufs, double? LoudnessRangeLu, double? TruePeakDb);

/// <summary>Locally measured editing style of a reference video. Only these numbers reach an AI provider — never the media.</summary>
public sealed record ReferenceStyleProfile(string SchemaVersion, double DurationSeconds, int Width, int Height,
    string AspectRatio, string Orientation, double FrameRate, bool HasAudio, string Pacing, ShotStatistics Shots,
    LoudnessStatistics Loudness, double? SpokenWordsPerMinute, IReadOnlyList<double> CutTimestamps,
    IReadOnlyList<string> Summary, DateTimeOffset DerivedAt);

/// <summary>Derives a reusable editing-style profile from a reference video using FFprobe and FFmpeg only.</summary>
public sealed partial class ReferenceStyleAnalyzer(IProcessRunner runner, IOptions<AppOptions> options)
{
    public const string SchemaVersion = "1.0";
    private const double SceneThreshold = 0.3;
    private const int MaxReportedCutTimestamps = 400;

    public async Task<ReferenceStyleProfile> AnalyzeAsync(string videoPath, string? subtitlePath,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var media = await ProbeAsync(videoPath, workingDirectory, cancellationToken);
        var cuts = await DetectCutsAsync(videoPath, workingDirectory, media.DurationSeconds, cancellationToken);
        var loudness = media.HasAudio
            ? await MeasureLoudnessAsync(videoPath, workingDirectory, cancellationToken)
            : new LoudnessStatistics(null, null, null);
        var shots = SummarizeShots(cuts, media.DurationSeconds);
        var wordsPerMinute = ReadSpokenWordsPerMinute(subtitlePath, media.DurationSeconds);
        var pacing = ClassifyPacing(shots.CutsPerMinute);
        var profile = new ReferenceStyleProfile(SchemaVersion, media.DurationSeconds, media.Width, media.Height,
            DescribeAspectRatio(media.Width, media.Height), DescribeOrientation(media.Width, media.Height),
            media.FrameRate, media.HasAudio, pacing, shots, loudness, wordsPerMinute,
            cuts.Take(MaxReportedCutTimestamps).ToList(), [], DateTimeOffset.UtcNow);
        return profile with { Summary = Describe(profile) };
    }

    private sealed record MediaFacts(double DurationSeconds, int Width, int Height, double FrameRate, bool HasAudio);

    private async Task<MediaFacts> ProbeAsync(string videoPath, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessInvocation(options.Value.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration",
                "-show_entries", "stream=codec_type,width,height,avg_frame_rate,duration",
                "-of", "json", videoPath], workingDirectory), cancellationToken);
        if (result.TimedOut) throw new TimeoutException("FFprobe timed out while inspecting the reference video.");
        if (result.ExitCode != 0) throw new InvalidOperationException($"FFprobe could not read the reference video: {result.StandardError}");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamNode) && streamNode.ValueKind == JsonValueKind.Array
            ? streamNode.EnumerateArray().ToArray() : [];
        var video = streams.FirstOrDefault(x => Text(x, "codec_type") == "video");
        if (video.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The reference file has no video stream.");
        var duration = ParseDouble(root.TryGetProperty("format", out var format) ? Text(format, "duration") : null)
            ?? ParseDouble(Text(video, "duration")) ?? 0;
        if (duration <= 0) throw new InvalidOperationException("The reference video reports no usable duration.");
        return new MediaFacts(duration,
            video.TryGetProperty("width", out var width) && width.ValueKind == JsonValueKind.Number ? width.GetInt32() : 0,
            video.TryGetProperty("height", out var height) && height.ValueKind == JsonValueKind.Number ? height.GetInt32() : 0,
            ParseFrameRate(Text(video, "avg_frame_rate")),
            streams.Any(x => Text(x, "codec_type") == "audio"));
    }

    /// <summary>Runs FFmpeg scene detection and returns the timestamp of every detected cut.</summary>
    private async Task<IReadOnlyList<double>> DetectCutsAsync(string videoPath, string workingDirectory,
        double durationSeconds, CancellationToken cancellationToken)
    {
        var threshold = SceneThreshold.ToString("0.00", CultureInfo.InvariantCulture);
        var result = await runner.RunAsync(new ProcessInvocation(options.Value.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", videoPath, "-an", "-sn",
                "-filter:v", $"select='gt(scene,{threshold})',metadata=print:file=-", "-f", "null", "-"],
            workingDirectory), cancellationToken);
        if (result.TimedOut) throw new TimeoutException("FFmpeg timed out while detecting cuts in the reference video.");
        if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg scene detection failed: {result.StandardError}");
        var timestamps = ReadTimestamps(result.StandardOutput);
        if (timestamps.Count == 0) timestamps = ReadTimestamps(result.StandardError);
        return timestamps.Where(x => x > 0.05 && x < durationSeconds).Distinct().Order().ToList();
    }

    private static List<double> ReadTimestamps(string output) => PtsTimeRegex().Matches(output)
        .Select(match => ParseDouble(match.Groups[1].Value)).OfType<double>().ToList();

    private async Task<LoudnessStatistics> MeasureLoudnessAsync(string videoPath, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessInvocation(options.Value.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", videoPath, "-vn", "-sn", "-af", "ebur128=peak=true", "-f", "null", "-"],
            workingDirectory), cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut) return new LoudnessStatistics(null, null, null);
        // The summary block repeats keys used by per-frame progress lines, so parse from "Summary:" onwards when present.
        var output = result.StandardError;
        var summaryIndex = output.LastIndexOf("Summary:", StringComparison.Ordinal);
        var scope = summaryIndex >= 0 ? output[summaryIndex..] : output;
        return new LoudnessStatistics(LastMatch(scope, IntegratedLoudnessRegex()),
            LastMatch(scope, LoudnessRangeRegex()), LastMatch(scope, TruePeakRegex()));
    }

    private static double? LastMatch(string value, Regex regex)
    {
        var matches = regex.Matches(value);
        return matches.Count == 0 ? null : ParseDouble(matches[^1].Groups[1].Value);
    }

    internal static ShotStatistics SummarizeShots(IReadOnlyList<double> cuts, double durationSeconds)
    {
        var boundaries = new List<double> { 0 };
        boundaries.AddRange(cuts.Where(x => x > 0 && x < durationSeconds));
        boundaries.Add(durationSeconds);
        var lengths = boundaries.Zip(boundaries.Skip(1), (start, end) => end - start).Where(x => x > 0).ToArray();
        if (lengths.Length == 0) lengths = [durationSeconds];
        var ordered = lengths.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        return new ShotStatistics(cuts.Count, Round(cuts.Count / (durationSeconds / 60)), Round(lengths.Average()),
            Round(median), Round(ordered[0]), Round(ordered[^1]), Round(lengths[0]));
    }

    internal static string ClassifyPacing(double cutsPerMinute) => cutsPerMinute switch
    {
        >= 30 => "VeryFast",
        >= 18 => "Fast",
        >= 9 => "Medium",
        _ => "Slow"
    };

    /// <summary>Counts spoken words per minute from the reference subtitle track when one was captured.</summary>
    internal static double? ReadSpokenWordsPerMinute(string? subtitlePath, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath) || durationSeconds <= 0) return null;
        try
        {
            var spoken = File.ReadLines(subtitlePath)
                .Where(line => !string.IsNullOrWhiteSpace(line) && !SubtitleCueRegex().IsMatch(line))
                .Select(line => SubtitleTagRegex().Replace(line, " "));
            var words = spoken.Sum(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Count(word => word.Any(char.IsLetterOrDigit)));
            return words == 0 ? null : Round(words / (durationSeconds / 60));
        }
        catch (IOException) { return null; }
    }

    internal static IReadOnlyList<string> Describe(ReferenceStyleProfile profile)
    {
        var lines = new List<string>
        {
            $"{profile.Orientation} {profile.Width}x{profile.Height} ({profile.AspectRatio}) at {profile.FrameRate:0.##} fps, {profile.DurationSeconds:0.#}s long.",
            $"{profile.Pacing} pacing: {profile.Shots.CutCount} cuts, {profile.Shots.CutsPerMinute:0.#} cuts per minute, {profile.Shots.AverageShotSeconds:0.##}s average shot.",
            $"Shot lengths run {profile.Shots.ShortestShotSeconds:0.##}s to {profile.Shots.LongestShotSeconds:0.##}s (median {profile.Shots.MedianShotSeconds:0.##}s).",
            $"The opening shot holds for {profile.Shots.OpeningShotSeconds:0.##}s before the first cut."
        };
        if (profile.Loudness.IntegratedLufs.HasValue)
            lines.Add($"Audio sits at {profile.Loudness.IntegratedLufs:0.#} LUFS integrated" +
                (profile.Loudness.LoudnessRangeLu.HasValue ? $" with {profile.Loudness.LoudnessRangeLu:0.#} LU range" : string.Empty) +
                (profile.Loudness.TruePeakDb.HasValue ? $" and a {profile.Loudness.TruePeakDb:0.#} dBFS true peak" : string.Empty) + ".");
        else if (!profile.HasAudio) lines.Add("The reference has no audio track.");
        if (profile.SpokenWordsPerMinute.HasValue)
            lines.Add($"Narration runs at roughly {profile.SpokenWordsPerMinute:0} words per minute.");
        return lines;
    }

    internal static string DescribeAspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "unknown";
        var divisor = Gcd(width, height);
        var (simpleWidth, simpleHeight) = (width / divisor, height / divisor);
        if (simpleWidth <= 32 && simpleHeight <= 32) return $"{simpleWidth}:{simpleHeight}";
        var ratio = (double)width / height;
        var known = new (string Label, double Value)[] { ("9:16", 9d / 16), ("1:1", 1), ("4:5", .8), ("16:9", 16d / 9), ("4:3", 4d / 3) };
        var closest = known.OrderBy(x => Math.Abs(x.Value - ratio)).First();
        return Math.Abs(closest.Value - ratio) < .02 ? closest.Label : $"{ratio:0.##}:1";
    }

    internal static string DescribeOrientation(int width, int height) => width <= 0 || height <= 0 ? "unknown"
        : width < height ? "Vertical" : width > height ? "Horizontal" : "Square";

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var property) && property.ValueKind != JsonValueKind.Null ? property.ToString() : null;
    private static double? ParseDouble(string? value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var parsed) && !double.IsNaN(parsed) ? parsed : null;

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 && ParseDouble(parts[0]) is { } numerator && ParseDouble(parts[1]) is { } denominator && denominator > 0)
            return Round(numerator / denominator);
        return ParseDouble(value) ?? 0;
    }

    [GeneratedRegex(@"pts_time:([0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex PtsTimeRegex();
    [GeneratedRegex(@"\bI:\s*(-?[0-9]+(?:\.[0-9]+)?)\s*LUFS")]
    private static partial Regex IntegratedLoudnessRegex();
    [GeneratedRegex(@"\bLRA:\s*(-?[0-9]+(?:\.[0-9]+)?)\s*LU")]
    private static partial Regex LoudnessRangeRegex();
    [GeneratedRegex(@"\bPeak:\s*(-?[0-9]+(?:\.[0-9]+)?)\s*dBFS")]
    private static partial Regex TruePeakRegex();
    [GeneratedRegex(@"^\s*(?:\d+\s*$|\d{1,2}:\d{2}:\d{2}[.,]\d{1,3}\s*-->)")]
    private static partial Regex SubtitleCueRegex();
    [GeneratedRegex(@"<[^>]{1,200}>|\{\\[^}]{1,200}\}")]
    private static partial Regex SubtitleTagRegex();
}

public static class ReferenceStyleJson
{
    public static string Serialize(ReferenceStyleProfile profile) => JsonSerializer.Serialize(profile, JsonDefaults.Serializer);
    public static ReferenceStyleProfile? Deserialize(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ReferenceStyleProfile>(json, JsonDefaults.Serializer); }
        catch (JsonException) { return null; }
    }
}
