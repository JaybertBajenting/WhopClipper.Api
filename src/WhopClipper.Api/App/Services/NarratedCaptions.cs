using System.Globalization;
using System.Text;

namespace WhopClipper.Api.App.Services;

public static partial class CaptionBuilder
{
    /// <summary>
    /// The colours @mambafx.luxury cycles its one-word captions through, as ASS BGR: white, cyan, yellow, white,
    /// green. A word keeps its colour; the next word takes the next one.
    /// </summary>
    private static readonly string[] NarratedColours = ["&HFFFFFF", "&HF2E83C", "&H33DFFF", "&HFFFFFF", "&H4DFF63"];

    /// <summary>Captions sit a little above the window's centre, where the reference puts them.</summary>
    public const int NarratedCaptionY = 860;

    /// <summary>
    /// One word at a time for the narrated-luxury style, spoken case kept ("billionaire", "$184,000"), in a bold sans
    /// with a soft shadow and no box. Words that land close together still each get a readable moment, and nothing
    /// shows after <paramref name="clipLength"/>.
    /// </summary>
    public static string BuildNarratedAss(IReadOnlyList<TranscriptWord> words, double clipLength,
        string fontName = "Segoe UI Bold", int fontSize = 62)
    {
        var tokens = MergeNumberUnits(words.OrderBy(x => x.Start)
            .Select(x => x with { Word = SeamClean(x.Word) })
            .Where(x => x.Word.Length > 0)
            .ToList());
        var font = fontName.Replace(",", " ").Trim();
        if (font.Length == 0) font = "Segoe UI Bold";
        var builder = new StringBuilder($"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: {PlayResX}
            PlayResY: {PlayResY}
            WrapStyle: 2
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Word,{font},{fontSize},&H00FFFFFF,&H000000FF,&H00101010,&H96000000,-1,0,0,0,100,100,0,0,1,3,3,5,60,60,0,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Text

            """.Replace("\r\n", "\n"));
        if (tokens.Count == 0 || clipLength <= 0) return builder.ToString();

        var starts = new double[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
            starts[index] = index == 0 ? Math.Max(0, tokens[index].Start)
                : Math.Max(tokens[index].Start, starts[index - 1] + MinimumWordSeconds);
        for (var index = 0; index < tokens.Count; index++)
        {
            var start = starts[index];
            if (start >= clipLength) break;
            var end = index + 1 < tokens.Count ? starts[index + 1]
                : Math.Max(start + MinimumWordSeconds, tokens[index].End + TailPaddingSeconds);
            // A long pause between two words leaves the screen clear rather than holding a stale word.
            end = Math.Min(end, Math.Max(tokens[index].End + TailPaddingSeconds, start + 0.3));
            end = Math.Min(end, clipLength);
            if (end <= start) continue;
            var colour = NarratedColours[index % NarratedColours.Length];
            var text = Escape(Censor(tokens[index].Word));
            builder.Append(CultureInfo.InvariantCulture,
                $"Dialogue: 0,{Stamp(start)},{Stamp(end)},Word,,0,0,0,{{\\pos({PlayResX / 2},{NarratedCaptionY})\\1c{colour}&}}{text}\n");
        }
        return builder.ToString();
    }
}
