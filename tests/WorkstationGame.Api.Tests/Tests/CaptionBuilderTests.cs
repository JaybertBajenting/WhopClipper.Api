using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class CaptionBuilderTests
{
    private static readonly TranscriptWord[] Words =
    [
        new(10.0, 10.4, "All"),
        new(10.4, 10.8, "right"),
        new(10.8, 11.6, "I'm"),
        new(11.6, 12.2, "going"),
        new(12.2, 12.6, "from"),
        new(12.6, 13.0, "the"),
        new(13.0, 13.6, "side"),
        // outside the clip window used below
        new(30.0, 30.5, "later"),
    ];

    private static string Build(CaptionStyle style = CaptionStyle.WordByWord) =>
        CaptionBuilder.BuildAss(Words, startSeconds: 10, endSeconds: 14, style);

    [Fact]
    public void PutsExactlyOneWordOnScreenAtATime()
    {
        var dialogue = Build().Split('\n').Where(x => x.StartsWith("Dialogue:")).ToArray();

        Assert.Equal(7, dialogue.Length);
        Assert.All(dialogue, line => Assert.DoesNotContain(' ', line.Split(",,").Last()));
    }

    [Fact]
    public void RebasesTimingsSoTheClipStartsAtZero()
    {
        var first = Build().Split('\n').First(x => x.StartsWith("Dialogue:"));

        // The first word begins 10s into the source, which is 0s into the clip.
        Assert.Contains("0:00:00.00", first);
        Assert.Contains("ALL", first);
    }

    [Fact]
    public void LeavesOutWordsSpokenOutsideTheClip()
    {
        Assert.DoesNotContain("LATER", Build());
    }

    /// <summary>
    /// Two events that overlap in time are both drawn, stacked, which reads as a doubled
    /// caption. Whisper regularly times consecutive words closer together than the minimum
    /// on-screen duration, so the minimum must never push an event past its successor.
    /// </summary>
    [Fact]
    public void NeverPutsTwoWordsOnScreenAtOnceWhenWordsAreTimedCloseTogether()
    {
        TranscriptWord[] rapid =
        [
            new(10.00, 10.02, "pack"),   // whisper timed these three
            new(10.00, 10.04, "number"), // within a couple of milliseconds
            new(10.02, 10.90, "two"),
            new(11.00, 11.40, "here"),
        ];

        var spans = CaptionBuilder.BuildAss(rapid, startSeconds: 10, endSeconds: 14)
            .Split('\n').Where(x => x.StartsWith("Dialogue:"))
            .Select(x => x.Split(',')).Select(f => (Start: f[1], End: f[2])).ToArray();

        Assert.Equal(4, spans.Length);
        for (var index = 1; index < spans.Length; index++)
            Assert.True(string.CompareOrdinal(spans[index].Start, spans[index - 1].End) >= 0,
                $"Event {index} starts at {spans[index].Start} before event {index - 1} ends at {spans[index - 1].End}");
    }

    [Fact]
    public void StripsSentencePunctuationButKeepsToneAndApostrophes()
    {
        TranscriptWord[] punctuated =
        [
            new(10.0, 10.4, "stuff."),
            new(10.4, 10.8, "sec,"),
            new(10.8, 11.2, "what's"),
            new(11.2, 11.6, "really?"),
        ];

        var text = CaptionBuilder.BuildAss(punctuated, startSeconds: 10, endSeconds: 14);

        Assert.Contains("STUFF\n", text);
        Assert.Contains("SEC\n", text);
        Assert.Contains("WHAT'S\n", text);
        Assert.Contains("REALLY?\n", text);
    }

    /// <summary>
    /// Text is the ninth and last Events field, so it keeps every comma after the eighth
    /// separator. An extra one puts a stray comma in front of every word on screen.
    /// </summary>
    [Fact]
    public void WritesNineEventFieldsSoTheTextIsNotPrefixedWithASeparator()
    {
        var dialogue = Build().Split('\n').Where(x => x.StartsWith("Dialogue:")).ToArray();

        Assert.All(dialogue, line =>
        {
            var text = string.Join(',', line.Split(',').Skip(8));
            Assert.False(text.StartsWith(','), $"Caption text was prefixed with a separator: '{text}'");
        });
        Assert.Contains("Caption,,0,0,0,ALL", dialogue[0]);
    }

    /// <summary>A caption that disappears between words makes the clip look broken.</summary>
    [Fact]
    public void HoldsEachWordUntilTheNextOneStarts()
    {
        var dialogue = Build().Split('\n').Where(x => x.StartsWith("Dialogue:")).ToArray();
        var firstEnd = dialogue[0].Split(',')[2];
        var secondStart = dialogue[1].Split(',')[1];

        Assert.Equal(firstEnd, secondStart);
    }

    [Fact]
    public void CentresTheCaptionRatherThanPinningItToTheBottom()
    {
        var style = Build().Split('\n').First(x => x.StartsWith("Style: Caption"));
        var fields = style.Split(',');

        // Alignment is field 19 in the V4+ style format; 5 is middle-centre.
        Assert.Equal("5", fields[18].Trim());
        Assert.Contains("Arial Black", style);
    }

    [Fact]
    public void UsesUppercaseForTheShortFormLook()
    {
        Assert.Contains("GOING", Build());
        Assert.DoesNotContain("going", Build());
    }

    [Fact]
    public void PhraseStyleGroupsWordsInsteadOfOnePerCard()
    {
        var dialogue = Build(CaptionStyle.Phrase).Split('\n').Where(x => x.StartsWith("Dialogue:")).ToArray();

        Assert.True(dialogue.Length < 7);
        Assert.Contains(dialogue, line => line.Split(",,").Last().Contains(' '));
    }

    [Fact]
    public void ProducesAValidHeaderEvenWhenNoWordsFallInsideTheClip()
    {
        var ass = CaptionBuilder.BuildAss(Words, startSeconds: 100, endSeconds: 110);

        Assert.Contains("[Events]", ass);
        Assert.DoesNotContain("Dialogue:", ass);
    }

    [Fact]
    public void ReadsAFasterWhisperTranscript()
    {
        var transcript = CaptionBuilder.ReadTranscript(
            """{"language":"en","duration":33.5,"words":[{"start":1.0,"end":1.4,"word":" Hello"}]}""");

        Assert.NotNull(transcript);
        Assert.Equal("en", transcript!.Language);
        Assert.Single(transcript.Words);
        // faster-whisper prefixes words with a space; it must not reach the caption.
        Assert.Contains("HELLO", CaptionBuilder.BuildAss(transcript.Words, 0, 5));
    }

    [Fact]
    public void ReturnsNullForUnreadableTranscriptJson()
    {
        Assert.Null(CaptionBuilder.ReadTranscript("not json"));
    }
}
