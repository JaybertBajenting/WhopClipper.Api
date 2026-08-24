using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

public sealed record TranscriptWord(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("word")] string Word);

public sealed record WordTranscript(
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("words")] IReadOnlyList<TranscriptWord> Words);

public enum CaptionStyle { WordByWord, Phrase }

/// <summary>
/// How the caption separates itself from the footage. Outline strokes the glyphs; Box paints a
/// solid card behind them, which is what short-form edits overwhelmingly use because it stays
/// readable over any background instead of fighting a busy one.
/// </summary>
public enum CaptionBackdrop { Outline, Box }

/// <summary>
/// Builds burned-in captions from word-level timings. Short-form captions are one word at a
/// time, centred, and large enough to read on a phone; a sentence-per-card subtitle track
/// reads as a documentary and is not what these clips want.
/// </summary>
public static class CaptionBuilder
{
    private const int PlayResX = 1080;
    private const int PlayResY = 1920;

    /// <summary>Advances the last word to the clip end so the caption never blinks out early.</summary>
    private const double TailPaddingSeconds = 0.35;
    private const double MinimumWordSeconds = 0.12;
    /// <summary>How much of a word may already have been spoken before the clip starts and still
    /// be captioned. Beyond this the viewer only hears its tail, so showing it reads as early.</summary>
    private const double LeadingWordGraceSeconds = 0.15;

    public static WordTranscript? ReadTranscript(string json)
    {
        try { return JsonSerializer.Deserialize<WordTranscript>(json, JsonDefaults.Serializer); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Renders an ASS file covering <paramref name="startSeconds"/>..<paramref name="endSeconds"/>
    /// of the source, with timings rebased so 0 is the start of the clip.
    /// </summary>
    public static string BuildAss(IReadOnlyList<TranscriptWord> words, double startSeconds, double endSeconds,
        CaptionStyle style = CaptionStyle.WordByWord, int fontSize = 96, string highlight = "&H00FFFFFF",
        CaptionBackdrop backdrop = CaptionBackdrop.Outline)
    {
        var clipLength = Math.Max(0, endSeconds - startSeconds);
        var inRange = words
            .Where(x => x.End > startSeconds && x.Start < endSeconds)
            // A word already under way when the clip opens gets clamped to time 0 below, which
            // puts its caption on the first frame although most of it was spoken before the cut.
            // The viewer hears the tail and sees the next word arrive too early, so the caption
            // reads as running ahead of the audio. Drop it unless the clip caught almost all of it.
            .Where(x => x.Start >= startSeconds - LeadingWordGraceSeconds)
            .OrderBy(x => x.Start)
            .Select(x => new TranscriptWord(
                Math.Max(0, x.Start - startSeconds),
                Math.Min(clipLength, x.End - startSeconds),
                Clean(x.Word)))
            .Where(x => x.Word.Length > 0)
            .ToList();

        var builder = new StringBuilder();
        builder.Append(Header(fontSize, highlight, backdrop));
        if (inRange.Count == 0) return builder.ToString();

        var events = style == CaptionStyle.WordByWord ? BuildWordEvents(inRange, clipLength) : BuildPhraseEvents(inRange, clipLength);
        foreach (var (start, end, text) in events)
            builder.Append(CultureInfo.InvariantCulture,
                // Nine fields, matching the Events format: Layer, Start, End, Style, Name,
                // MarginL, MarginR, MarginV, Text. Text is the last field and swallows every
                // remaining comma, so one spare separator here prefixes every caption with it.
                $"Dialogue: 0,{Stamp(start)},{Stamp(end)},Caption,,0,0,0,{text}\n");
        return builder.ToString();
    }

    /// <summary>
    /// One word per event. Each word holds until the next begins, so there is never a gap
    /// where the screen is blank mid-sentence.
    /// </summary>
    private static List<(double Start, double End, string Text)> BuildWordEvents(
        IReadOnlyList<TranscriptWord> words, double clipLength)
    {
        // Starts are forced to climb by at least a minimum step first. Applying that minimum to
        // an event's *end* instead pushes it past the next word's start whenever two words are
        // timed closer together than the minimum, and libass stacks overlapping events - which
        // is what puts two words on screen at once. Cascading the starts keeps the run strictly
        // sequential, and it self-corrects at the next pause in speech.
        var starts = new double[words.Count];
        for (var index = 0; index < words.Count; index++)
            starts[index] = index == 0
                ? Math.Max(0, words[index].Start)
                : Math.Max(words[index].Start, starts[index - 1] + MinimumWordSeconds);

        var events = new List<(double, double, string)>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            var start = starts[index];
            if (start >= clipLength) break;
            var end = index + 1 < words.Count
                ? starts[index + 1]
                : Math.Max(start + MinimumWordSeconds, words[index].End + TailPaddingSeconds);
            if (end > clipLength) end = clipLength;
            if (end <= start) continue;
            events.Add((start, end, Escape(words[index].Word.ToUpperInvariant())));
        }
        return events;
    }

    /// <summary>Groups words into short phrases for the calmer alternative style.</summary>
    private static List<(double Start, double End, string Text)> BuildPhraseEvents(
        IReadOnlyList<TranscriptWord> words, double clipLength)
    {
        const int maxWords = 4;
        const double maxSeconds = 2.2;
        var events = new List<(double, double, string)>();
        var bucket = new List<TranscriptWord>();

        void Flush()
        {
            if (bucket.Count == 0) return;
            var start = bucket[0].Start;
            var end = Math.Min(clipLength, bucket[^1].End + 0.1);
            if (end > start) events.Add((start, end, Escape(string.Join(' ', bucket.Select(x => x.Word)).ToUpperInvariant())));
            bucket.Clear();
        }

        foreach (var word in words)
        {
            if (bucket.Count > 0 && (bucket.Count >= maxWords || word.End - bucket[0].Start > maxSeconds)) Flush();
            bucket.Add(word);
        }
        Flush();
        return events;
    }

    /// <summary>
    /// Alignment 5 with a zero margin centres the caption in the frame rather than sitting it
    /// on the bottom edge, which is where a phone's UI chrome lives.
    ///
    /// <see cref="CaptionBackdrop.Box"/> switches BorderStyle to 3, where the outline value
    /// becomes padding around an opaque box rather than a stroke around the glyphs, and
    /// BackColour paints that box. ASS alpha is inverted - 00 is fully opaque - so the box
    /// colour is &amp;H00000000 rather than the half-transparent &amp;H80000000 the outline
    /// style uses for its drop shadow.
    /// </summary>
    private static string Header(int fontSize, string highlight, CaptionBackdrop backdrop)
    {
        var (borderStyle, outline, shadow, backColour) = backdrop == CaptionBackdrop.Box
            ? (3, 14, 0, "&H00000000")
            : (1, 6, 3, "&H80000000");
        return $"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: {PlayResX}
            PlayResY: {PlayResY}
            WrapStyle: 2
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Caption,Arial Black,{fontSize},{highlight},&H000000FF,&H00000000,{backColour},-1,0,0,0,100,100,0,0,{borderStyle},{outline},{shadow},5,60,60,0,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Text

            """.Replace("\r\n", "\n");
    }

    private static string Stamp(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}");
    }

    /// <summary>Strips the leading space faster-whisper emits and any stray control characters.</summary>
    /// <summary>
    /// Sentence punctuation that carries no meaning once a word is alone on screen. Question
    /// and exclamation marks are deliberately absent - those carry tone and are worth keeping.
    /// </summary>
    private const string SilentPunctuation = ".,;:…—\"";

    private static string Clean(string word)
    {
        var trimmed = word.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
            if (!char.IsControl(character)) builder.Append(character);
        // Only trim the edges: the apostrophe in WHAT'S has to survive.
        while (builder.Length > 0 && SilentPunctuation.Contains(builder[^1])) builder.Length--;
        var lead = 0;
        while (lead < builder.Length && SilentPunctuation.Contains(builder[lead])) lead++;
        return builder.ToString(lead, builder.Length - lead);
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\")
        .Replace("{", "\\{")
        .Replace("}", "\\}")
        .Replace("\n", " ");
}
