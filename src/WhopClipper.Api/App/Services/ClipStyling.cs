using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <param name="StartSeconds">May differ from the requested window: a clip is pulled onto
/// speech boundaries so it does not open or close mid-thought.</param>
public sealed record ClipStyle(double StartSeconds, double EndSeconds,
    IReadOnlyList<SoundEffectPlacementDto> SoundEffects, IReadOnlyList<ColorFlashDto> ColorFlashes,
    IReadOnlyList<ZoomPunchDto> ZoomPunches, Guid? MusicAssetId, double MusicVolume);

/// <summary>
/// Decides where the sound effects, colour flashes and punch-ins go, and where the clip should
/// start and stop.
///
/// Every part of this is a rule learned the hard way on the Fexr campaign:
///
/// - Cues sit on measured audio onsets. Effects placed at fixed offsets hit nothing and read as
///   awkward.
/// - The vine boom goes on the *punchline*, which is not the loudest moment. A heckler on voice
///   chat is quieter than the streamer on a desk mic, so ranking by loudness put the boom on his
///   calm reply and gave the insult a soft alert.
/// - A colour wash only survives if its onset clears <see cref="RedMinimumDb"/>. Flashing on
///   every cue is what made the first batch look random.
/// - A clip ends on a finished sentence. Trimming to the widest pause ends it on hesitation.
/// </summary>
public sealed class ClipStylist(IProcessRunner runner, IOptions<AppOptions> options,
    ILogger<ClipStylist> logger)
{
    public const double RedMinimumDb = 35.0;
    public const double GreenMinimumDb = 40.0;
    private const double MinimumClipSeconds = 12.0;
    private const double MaximumClipSeconds = 19.5;
    private const double MinimumBeatSpacing = 2.2;
    private const int MaximumBeats = 3;
    private const double MusicVolume = 0.32;

    private static readonly Regex SentenceEnd = new(@"[.!?]$", RegexOptions.Compiled);
    /// <summary>Ending on one of these is mid-thought however long the pause is.</summary>
    private static readonly HashSet<string> Dangling = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "but", "or", "if", "that", "this", "to", "of", "in", "on", "at",
        "is", "are", "was", "were", "am", "be", "been", "my", "your", "his", "her", "their", "its",
        "our", "i", "you", "he", "she", "we", "they", "it", "who", "what", "when", "where", "how",
        "why", "so", "because", "just", "like", "got", "get", "have", "has", "had", "do", "does",
        "did", "with", "for", "from", "as", "than", "then", "some", "any", "not", "no", "very",
        "really", "about", "into", "up", "out",
    };

    /// <summary>Only these two ever carry a wash, and the boom is always red.</summary>
    private static readonly string[] BoomNames = ["vine boom"];
    private static readonly string[] GreenNames = ["bua", "awkward"];
    private static readonly string[] QuietNames = ["fahh", "twitch", "cash", "undertaker", "bell"];

    public async Task<ClipStyle> BuildAsync(string mediaPath, double startSeconds, double endSeconds,
        IReadOnlyList<TranscriptWord> words, IReadOnlyList<MediaLibraryAsset> assets, int rotation,
        CancellationToken cancellationToken)
    {
        var (start, end) = ChooseWindow(words, startSeconds, endSeconds);
        var music = Pick(assets, MediaAssetKind.Music, rotation);
        var envelope = await OnsetEnvelopeAsync(mediaPath, start, end - start, cancellationToken);
        if (envelope.Length == 0)
            return new ClipStyle(start, end, [], [], [], music, music is null ? 0 : MusicVolume);

        var beats = PickBeats(envelope, words, start, end - start);
        var effects = new List<SoundEffectPlacementDto>();
        var flashes = new List<ColorFlashDto>();
        var zooms = new List<ZoomPunchDto>();

        for (var rank = 0; rank < beats.Count; rank++)
        {
            var (at, strength) = beats[rank];
            var asset = rank switch
            {
                0 => Pick(assets, MediaAssetKind.SoundEffect, 0, BoomNames),
                1 => Pick(assets, MediaAssetKind.SoundEffect, 0, GreenNames),
                _ => Pick(assets, MediaAssetKind.SoundEffect, rotation, QuietNames),
            };
            if (asset is null) continue;
            effects.Add(new SoundEffectPlacementDto(asset.Value, at, -4));
            if (rank == 0 && strength >= RedMinimumDb)
                flashes.Add(new ColorFlashDto(at, 0.5, 0.45, "Red"));
            else if (rank == 1 && strength >= GreenMinimumDb)
                flashes.Add(new ColorFlashDto(at, 0.5, 0.45, "Green"));
        }

        // Zooms are a separate tool from flashes - in the reference clips only 3 of 12 landed
        // near one - so the punch goes on the strongest beat that did not earn a wash.
        var flashed = flashes.Select(x => x.AtSeconds).ToHashSet();
        var punch = beats.FirstOrDefault(x => !flashed.Contains(x.At));
        if (punch.At > 0) zooms.Add(new ZoomPunchDto(punch.At, 0.5, 1.15));

        logger.LogInformation("Styled {Start:0.##}-{End:0.##}s: {Effects} cues, {Flashes} flashes, {Zooms} punches",
            start, end, effects.Count, flashes.Count, zooms.Count);
        return new ClipStyle(start, end, effects.OrderBy(x => x.AtSeconds).ToList(),
            flashes.OrderBy(x => x.AtSeconds).ToList(), zooms, music, music is null ? 0 : MusicVolume);
    }

    /// <summary>
    /// Pulls the requested window onto speech boundaries: never opening mid-word, and ending on
    /// a full stop where one exists or an audible breath where it does not. Whisper punctuation
    /// collapses on messy multi-speaker audio, so the fallback matters.
    /// </summary>
    internal static (double Start, double End) ChooseWindow(IReadOnlyList<TranscriptWord> words,
        double requestedStart, double requestedEnd)
    {
        if (words.Count == 0) return (requestedStart, requestedEnd);
        var opening = words.FirstOrDefault(x => x.Start >= requestedStart - 0.35);
        var start = opening is null ? requestedStart : Math.Max(0, opening.Start - 0.25);

        var inside = words.Where(x => x.End > start && x.Start < start + MaximumClipSeconds + 1)
            .OrderBy(x => x.Start).ToList();
        (int Quality, double End)? best = null;
        for (var index = 0; index < inside.Count; index++)
        {
            var relative = inside[index].End - start;
            if (relative < MinimumClipSeconds || relative > MaximumClipSeconds) continue;
            var gap = index + 1 < inside.Count ? inside[index + 1].Start - inside[index].End : 2.0;
            var word = inside[index].Word.Trim();
            int quality;
            double tail;
            // The tail must stay shorter than the gap or the next word's first syllable is
            // dragged in, which is what made clips sound like they stopped mid-phrase.
            if (SentenceEnd.IsMatch(word) && gap >= 0.15) { quality = gap >= 0.35 ? 3 : 2; tail = Math.Min(0.45, gap - 0.08); }
            else if (gap >= 0.45 && !Dangling.Contains(Strip(word))) { quality = 1; tail = Math.Min(0.5, gap * 0.5); }
            else continue;
            if (best is null || quality > best.Value.Quality)
                best = (quality, relative + tail);
        }
        return best is null ? (start, requestedEnd) : (start, start + best.Value.End);
    }

    private static string Strip(string word) => Regex.Replace(word, "[^a-zA-Z']", string.Empty);

    private List<(double At, double Strength)> PickBeats(double[] rise,
        IReadOnlyList<TranscriptWord> words, double start, double duration)
    {
        var beats = new List<(double At, double Strength)>();
        foreach (var index in Enumerable.Range(0, rise.Length).OrderByDescending(i => rise[i]))
        {
            if (rise[index] < 6) break;
            var at = Math.Round(index * 0.02, 2);
            if (at < 0.6 || at > duration - 1.2) continue;
            if (beats.Any(b => Math.Abs(b.At - at) < MinimumBeatSpacing)) continue;
            // Gate on speech: a cue landing on room noise has nothing to punctuate.
            if (!words.Any(w => w.Start >= start + at - 0.5 && w.Start <= start + at + 0.7)) continue;
            beats.Add((at, rise[index]));
            if (beats.Count >= MaximumBeats) break;
        }
        return beats;
    }

    /// <summary>dB rise over the median of the previous half second, on a 20 ms grid.</summary>
    private async Task<double[]> OnsetEnvelopeAsync(string mediaPath, double start, double duration,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"clipstyle-{Guid.NewGuid():N}.wav");
        try
        {
            var invariant = CultureInfo.InvariantCulture;
            var result = await runner.RunAsync(new ProcessInvocation(options.Value.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                 "-ss", start.ToString("0.###", invariant), "-t", duration.ToString("0.###", invariant),
                 "-i", mediaPath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", temp],
                Path.GetTempPath(), Timeout: TimeSpan.FromMinutes(3)), cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(temp)) return [];

            var bytes = await File.ReadAllBytesAsync(temp, cancellationToken);
            const int header = 44;
            var samples = (bytes.Length - header) / 2;
            if (samples <= 0) return [];
            const int hop = 16000 / 50;              // 20 ms
            var frames = samples / hop;
            var db = new double[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                var offset = header + frame * hop * 2;
                for (var i = 0; i < hop; i++)
                {
                    var value = BitConverter.ToInt16(bytes, offset + i * 2) / 32768.0;
                    sum += value * value;
                }
                db[frame] = 20 * Math.Log10(Math.Sqrt(sum / hop) + 1e-9);
            }

            var rise = new double[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                if (frame <= 25) { rise[frame] = -99; continue; }
                var window = db[(frame - 25)..frame].Order().ToArray();
                rise[frame] = db[frame] - window[window.Length / 2];
            }
            return rise;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static Guid? Pick(IReadOnlyList<MediaLibraryAsset> assets, MediaAssetKind kind, int rotation,
        string[]? names = null)
    {
        var pool = assets.Where(x => x.Kind == kind).ToList();
        if (names is not null)
            pool = pool.Where(x => names.Any(n => x.FileName.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (pool.Count == 0) return null;
        return pool[Math.Abs(rotation) % pool.Count].Id;
    }
}
