using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Captions for the podcast-split style, measured off the Double Coverage TV shorts: one word at a
/// time centred on the line between the two halves, white with a soft shadow and no box. Emphasis
/// is italic rather than a colour, the speaker's ? and ! stay, a number keeps its unit
/// ("$1.9 MILLION"), and swearing is starred out ("BAD***").
/// </summary>
public static partial class CaptionBuilder
{
    private static readonly HashSet<string> NumberUnits = new(StringComparer.OrdinalIgnoreCase)
        { "million", "billion", "thousand", "grand", "percent", "hundred", "k", "mil" };

    private const RegexOptions WordOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>First letter kept so the word still reads; the platforms mute or limit the rest.</summary>
    private static readonly (Regex Pattern, string Replacement)[] Profanity =
    [
        (new Regex("fuck", WordOptions), "f***"),
        (new Regex("shit", WordOptions), "sh*t"),
        (new Regex("bitch", WordOptions), "b*tch"),
        (new Regex("nigg(a|er)", WordOptions), "n****"),
        // Anchored to the whole word so "class", "pass" and "assist" are left alone.
        (new Regex(@"^(\W*)(bad|dumb|jack|smart|kick|half)?ass(hole|es)?(\W*)$", WordOptions), "$1$2***$3$4"),
        (new Regex(@"^(\W*)pussy", WordOptions), "$1p***y"),
        (new Regex(@"^(\W*)dick(s|head)?(\W*)$", WordOptions), "$1d**k$2$3"),
    ];

    /// <summary>
    /// Lays kept pieces end to end on one clock. A word belongs to the piece it starts in, and a
    /// word already under way when a piece opens is only kept if the piece caught nearly all of it.
    /// </summary>
    public static IReadOnlyList<TranscriptWord> RemapToClip(IReadOnlyList<TranscriptWord> words,
        IReadOnlyList<(double Start, double End)> pieces)
    {
        var result = new List<TranscriptWord>();
        var offset = 0.0;
        foreach (var (start, end) in pieces)
        {
            var length = end - start;
            if (length <= 0) continue;
            foreach (var word in words.Where(x => x.Start >= start - LeadingWordGraceSeconds && x.Start < end)
                         .OrderBy(x => x.Start))
                result.Add(new TranscriptWord(offset + Math.Max(0, word.Start - start),
                    offset + Math.Clamp(word.End - start, 0, length), word.Word));
            offset += length;
        }
        return result;
    }

    public static string BuildSeamAss(IReadOnlyList<TranscriptWord> clipWords, double clipLength,
        IEnumerable<string>? emphasis = null, string fontName = "Segoe UI Black", int fontSize = 84)
    {
        var emphasised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in emphasis ?? [])
            foreach (var part in phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (NormaliseForMatch(part) is { Length: > 0 } key) emphasised.Add(key);

        var tokens = MergeNumberUnits(clipWords.OrderBy(x => x.Start)
            .Select(x => x with { Word = SeamClean(x.Word) })
            .Where(x => x.Word.Length > 0)
            .ToList());

        var builder = new StringBuilder(SeamHeader(fontName, fontSize));
        if (tokens.Count == 0 || clipLength <= 0) return builder.ToString();

        // Same cascade as the streamer captions: starts climb by a minimum step, so two words timed
        // close together can never overlap and stack on screen.
        var starts = new double[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
            starts[index] = index == 0
                ? Math.Max(0, tokens[index].Start)
                : Math.Max(tokens[index].Start, starts[index - 1] + MinimumWordSeconds);

        var italicised = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            var start = starts[index];
            if (start >= clipLength) break;
            var end = index + 1 < tokens.Count
                ? starts[index + 1]
                : Math.Max(start + MinimumWordSeconds, tokens[index].End + TailPaddingSeconds);
            if (end > clipLength) end = clipLength;
            if (end <= start) continue;

            var text = Escape(Censor(tokens[index].Word).ToUpperInvariant());
            // The override tags go on after escaping, or Escape would neutralise them.
            if (IsEmphasis(tokens[index].Word, emphasised, italicised)) text = "{\\i1}" + text + "{\\i0}";
            builder.Append(CultureInfo.InvariantCulture,
                $"Dialogue: 0,{Stamp(start)},{Stamp(end)},Caption,,0,0,0,{text}\n");
        }
        return builder.ToString();
    }

    internal static string Censor(string word)
    {
        var text = word;
        foreach (var (pattern, replacement) in Profanity) text = pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>
    /// Keeps what carries tone and drops what only punctuates a sentence. A trailing ellipsis stays
    /// ("BUT..."); a single full stop, comma or colon does not.
    /// </summary>
    internal static string SeamClean(string word)
    {
        var text = new string(word.Trim().Where(c => !char.IsControl(c)).ToArray());
        text = text.Trim('"', '“', '”', '(', ')', '[', ']').Trim();
        while (text.Length > 0 && text[^1] is ',' or ';' or ':') text = text[..^1];
        if (text.EndsWith('.') && !text.EndsWith("..", StringComparison.Ordinal)) text = text[..^1];
        return text.Trim();
    }

    private static List<TranscriptWord> MergeNumberUnits(List<TranscriptWord> tokens)
    {
        var merged = new List<TranscriptWord>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            var current = tokens[index];
            if (index + 1 < tokens.Count && current.Word.Any(char.IsDigit)
                && NumberUnits.Contains(tokens[index + 1].Word.TrimEnd('?', '!', '.'))
                && tokens[index + 1].Start - current.End < 0.4)
            {
                merged.Add(new TranscriptWord(current.Start, tokens[index + 1].End,
                    current.Word + " " + tokens[index + 1].Word));
                index++;
                continue;
            }
            merged.Add(current);
        }
        return merged;
    }

    /// <summary>
    /// Numbers are always set in italics. A chosen word is italic only the first time it is said: the
    /// first rebuilt clip put 22 of its 105 captions in italics because repeated words kept qualifying,
    /// and emphasis on every repeat stops meaning anything.
    /// </summary>
    private static bool IsEmphasis(string token, HashSet<string> emphasised, HashSet<string> italicised)
    {
        if (token.Any(char.IsDigit)) return true;
        var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NormaliseForMatch).ToList();
        return parts.Any(emphasised.Contains) && italicised.Add(string.Join(' ', parts));
    }

    private static string NormaliseForMatch(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Alignment 5 at a 1920-high play resolution centres the word at y 960, exactly on the split.
    /// A thin black outline plus a half-transparent shadow reads over both the camera above and
    /// the footage below without the box the streamer style can use.
    /// </summary>
    private static string SeamHeader(string fontName, int fontSize)
    {
        var font = fontName.Replace(",", " ").Trim();
        if (font.Length == 0) font = "Arial Black";
        return $"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: {PlayResX}
            PlayResY: {PlayResY}
            WrapStyle: 2
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Caption,{font},{fontSize},&H00FFFFFF,&H000000FF,&H00000000,&H78000000,-1,0,0,0,100,100,0,0,1,4,3,5,60,60,0,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Text

            """.Replace("\r\n", "\n");
    }
}
