using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests.Tests;

public sealed class BrollRepairTests
{
    private static BrollShotDto Shot(double duration, string name) => new(name, 0, duration,
        new CropRectDto(0, 0, 810, 720), 1280, 720, [], name);

    [Fact]
    public void MarkedSecondsExpandToWholeShotsAndPreserveAllOtherShots()
    {
        var original = new[] { Shot(2, "first"), Shot(3, "bad"), Shot(2, "last") };
        var selection = BrollRepairSupport.Select(original, 2.4, 4.6);
        Assert.Equal(2, selection.StartSeconds);
        Assert.Equal(5, selection.EndSeconds);
        var updated = BrollRepairSupport.Replace(original, selection, [Shot(1.5, "new-a"), Shot(1.5, "new-b")]);
        Assert.Equal(new[] { "first", "new-a", "new-b", "last" }, updated.Select(x => x.Query));
        Assert.Equal(7, updated.Sum(x => x.DurationSeconds));
    }

    [Fact]
    public void DoesNotIncludeFollowingShotAtExactEndBoundary()
    {
        var selection = BrollRepairSupport.Select([Shot(2, "a"), Shot(2, "b")], .5, 2);
        Assert.Equal(0, selection.FirstShot);
        Assert.Equal(1, selection.LastShotExclusive);
    }

    [Fact]
    public void RejectsIncompleteReplacementAndOversizedMark()
    {
        var shots = new[] { Shot(12, "a"), Shot(12, "b") };
        Assert.Throws<ArgumentOutOfRangeException>(() => BrollRepairSupport.Select(shots, 0, 21));
        var selection = BrollRepairSupport.Select(shots, 1, 2);
        Assert.Throws<ArgumentException>(() => BrollRepairSupport.Replace(shots, selection, [Shot(2, "too-short")]));
    }

    [Fact]
    public void PatchCopiesAudioAndOnlyEnablesNewPanelInsideSelectedWindow()
    {
        var source = Path.GetTempFileName();
        try
        {
            var args = BrollRepairSupport.BuildFfmpegArguments("original.mp4", "candidate.mp4", "captions.ass",
                [Shot(3, source)], 4, 7, 20);
            var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
            Assert.Contains("gte(t,4)*lt(t,7)", graph);
            Assert.Contains("subtitles='captions.ass':alpha=1", graph);
            Assert.Contains("crop=1080:960:0:960", graph);
            Assert.Equal("copy", args[args.ToList().IndexOf("-c:a") + 1]);
            Assert.DoesNotContain("[musicbase]", graph);
        }
        finally { File.Delete(source); }
    }

    /// <summary>Words 0.3 s apart from <paramref name="at"/>, each 0.25 s long.</summary>
    private static List<TranscriptWord> Spoken(double at, string text) =>
        text.Split(' ').Select((word, index) => new TranscriptWord(at + index * 0.3, at + index * 0.3 + 0.25, word)).ToList();

    [Fact]
    public void ACutLandsOnTheWordsTheAiQuotesNotOnItsLineTime()
    {
        var words = Spoken(100, "so in hindsight I should have signed with Top Rank that was the move and then we went");
        // The AI's own times are a line off; its quotes name "I should have signed" ... "was the move".
        var snapped = SpliceSnapper.SnapToQuotes((100.2, 104.9), "I should have signed", "that was the move", words);
        Assert.Equal(100.9, snapped.Start, 3);   // "I"
        Assert.Equal(104.15, snapped.End, 3);    // end of "move"
    }

    [Fact]
    public void AQuoteThatIsNotInTheTranscriptKeepsTheAiTime()
    {
        var words = Spoken(100, "we went to Vegas that night and the whole team came");
        var snapped = SpliceSnapper.SnapToQuotes((100.2, 102.4), "nothing like this", "was ever said", words);
        Assert.Equal((100.2, 102.4), snapped);
    }

    [Fact]
    public void ALeadInBeforeASpliceIsDroppedButNotBeforeAContinuingTake()
    {
        var words = Spoken(100, "that was the move in hindsight")
            .Concat(Spoken(130, "we went to Vegas that night")).ToList();
        var spliced = SpliceSnapper.TrimDanglingEnds([(100, 101.8), (130, 131.8)], words);
        Assert.Equal(101.15, spliced[0].End, 3);   // ends on "move"
        Assert.Equal((130d, 131.8), spliced[1]);   // the last piece is never trimmed

        // 0.2 s apart is one take, not a splice: nothing is cut.
        var joined = SpliceSnapper.TrimDanglingEnds([(100, 101.8), (102.0, 103)], words);
        Assert.Equal(101.8, joined[0].End, 3);
    }

    [Fact]
    public void ASentenceEndingOnItOrSoIsNotTrimmed()
    {
        var words = Spoken(100, "honestly I think so").Concat(Spoken(130, "and we won")).ToList();
        var spliced = SpliceSnapper.TrimDanglingEnds([(100, 101.2), (130, 131)], words);
        Assert.Equal(101.2, spliced[0].End, 3);
    }

    [Fact]
    public void ALongSilenceInsideAPieceIsCutDownToAFifthOfASecond()
    {
        var words = new List<TranscriptWord>
        {
            new(10.0, 10.3, "he"), new(10.35, 10.6, "said"), new(10.65, 11.0, "no"),
            new(12.2, 12.5, "and"), new(12.55, 12.9, "left"),
        };
        var pieces = PieceSnapper.TightenPauses([(9.92, 13.02)], words);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(11.1, pieces[0].End, 3);
        Assert.Equal(12.1, pieces[1].Start, 3);
        // Short breaths between words stay: 0.05 s gaps are not pauses.
        Assert.Single(PieceSnapper.TightenPauses([(9.92, 11.1)], words));
    }

    [Fact]
    public void CondensedStoriesKeepTheirPieces()
    {
        var words = Spoken(200, "what happened when you signed").Concat(Spoken(230, "I told them I want the belt"))
            .Concat(Spoken(260, "and they gave me three fights")).ToList();
        var json = """
            {"stories":[{"title":"He wanted the belt 🥊","caption":"c","teller":"Big Mike","rationale":"r","score":0.9,"emphasis":[],
              "pieces":[
                {"startSeconds":200,"endSeconds":201.6,"firstWords":"what happened","lastWords":"you signed"},
                {"startSeconds":229,"endSeconds":232.5,"firstWords":"I told them","lastWords":"want the belt"},
                {"startSeconds":259,"endSeconds":262.0,"firstWords":"and they gave","lastWords":"three fights"}]}]}
            """;
        using var document = JsonDocument.Parse(json);
        // About 5 s of speech is under the 10 s minimum, so a longer third piece is checked below.
        Assert.Empty(StoryParser.ParseStories(document.RootElement, 3000, words));

        var longer = json.Replace("\"endSeconds\":262.0", "\"endSeconds\":272.0").Replace("three fights", "the rest");
        words.AddRange(Spoken(262.1, "and then the rest of it was history for me and my whole team the rest"));
        using var second = JsonDocument.Parse(longer);
        var story = Assert.Single(StoryParser.ParseStories(second.RootElement, 3000, words));
        Assert.Equal(3, story.Pieces.Count);
        Assert.Equal(200, story.Pieces[0].Start, 3);
        Assert.Equal(230, story.Pieces[1].Start, 3);   // "I" at 230, not the AI's 229
        Assert.Equal("Big Mike", story.Teller);
    }

    [Fact]
    public void ACondensedClipCoversTheEpisodeFromItsFirstPieceToItsLast()
    {
        // Jingle Jared's Oscar clip: 26.6 s kept out of 3140-3221 s of the episode (span clock + 3000 s offset).
        var plan = new WhopClipper.Api.App.Domain.EditPlan
        {
            StartSeconds = 3140.4, EndSeconds = 3167.0,
            ExtraSettingsJson = """
                {"podcastSplit":{"pieces":[{"startSeconds":140.4,"endSeconds":144.6},{"startSeconds":179.1,"endSeconds":201.0},
                  {"startSeconds":218.1,"endSeconds":221.6}],"episodeOffsetSeconds":3000}}
                """,
        };
        var span = StoryParser.EpisodeSpan(plan);
        Assert.Equal(3140.4, span.Start, 3);
        Assert.Equal(3221.6, span.End, 3);
        // The same story picked again later, as "How Ben Affleck got Mike Majlak BANNED", is now seen as taken.
        Assert.True(StoryParser.OverlapsClipped([(3178.8, 3189.4), (3210.5, 3221.8)], [span]));
        // Start + length alone ended at 3167 and let it through.
        Assert.False(StoryParser.OverlapsClipped([(3178.8, 3189.4), (3210.5, 3221.8)], [(plan.StartSeconds, plan.EndSeconds)]));
        // A plan from before condensing keeps its own stored range.
        Assert.Equal((10d, 40d), StoryParser.EpisodeSpan(new WhopClipper.Api.App.Domain.EditPlan { StartSeconds = 10, EndSeconds = 40 }));
    }

    [Fact]
    public void TheTellersSeatIsFoundFromAnyPartOfTheirName()
    {
        string[] couch = ["Mike Majlak", "Jingle Jared"];
        Assert.Equal((0, 2), CameraSeats.Find(couch, "Mike Majlak"));
        Assert.Equal((0, 2), CameraSeats.Find(couch, "Big Mike"));
        Assert.Equal((1, 2), CameraSeats.Find(couch, "Jared"));
        Assert.Null(CameraSeats.Find(couch, "Mystic Zach"));                  // the host has his own camera
        Assert.Null(CameraSeats.Find(couch, ""));
        Assert.Null(CameraSeats.Find(["Mike Majlak"], "Mike Majlak"));        // one seat: nothing to choose
        Assert.Null(CameraSeats.Find(["Mike Tyson", "Mike Majlak"], "Mike")); // two matches: no guess
        Assert.Null(CameraSeats.Find(["Big Cat", "Big Mike"], "Big Papi"));   // "big" alone is not a name
    }
}
