using System.Text.Json;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// The Double Coverage TV layout, measured across 25 of the channel's shorts: the frame splits at
/// y 960, the guest's camera is a fixed punch-in across the top, footage of what is being said
/// runs underneath for the whole clip, and the logo sits top-right of the guest half.
/// </summary>
public sealed class PodcastSplitRenderTests
{
    private static readonly string Root = Path.GetTempPath();

    private static FfmpegPodcastSplitSpec Spec(IReadOnlyList<FfmpegBrollShot>? broll = null,
        FfmpegLogoOverlay? logo = null, string? music = null, IReadOnlyList<FfmpegTopShot>? top = null,
        double musicStart = 0) => new(
        Path.Combine(Root, "span.mp4"), 1920, 1080, Path.Combine(Root, "out.mp4"), 30, 20,
        top ??
        [
            new FfmpegTopShot(2.5, 4, new PixelRect(472, 3, 960, 853)),
            new FfmpegTopShot(10, 3, new PixelRect(500, 0, 960, 853), [new PixelRect(900, 40, 120, 60)]),
        ],
        broll ??
        [
            new FfmpegBrollShot(Path.Combine(Root, "a.mp4"), 1, 2.5, new PixelRect(352, 0, 1215, 1080), 1920, 1080),
            new FfmpegBrollShot(Path.Combine(Root, "b.mp4"), 0, 4.5, new PixelRect(100, 0, 810, 720), 1280, 720,
                [new PixelRect(20, 800, 200, 80)]),
        ],
        Path.Combine(Root, "captions.ass"), music, 0.3, -10, logo, MusicStartSeconds: musicStart);

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void CameraPiecesAreCutFromTheSpanAndJoinedWithTheirOwnSound()
    {
        var args = FfmpegArgumentBuilder.BuildPodcastSplit(Spec());
        var list = args.ToList();
        var graph = Graph(args);

        // One fast-seeking input per piece, so each opens straight on its own moment.
        Assert.Equal(new[] { "-ss", "2.5", "-t", "4", "-i", Path.Combine(Root, "span.mp4") }, list.Skip(2).Take(6));
        Assert.Contains("[0:v]crop=960:853:472:3,scale=1080:960,setsar=1,fps=30,setpts=PTS-STARTPTS[tv0]", graph);
        Assert.Contains("[1:v]crop=960:853:500:0,scale=1080:960,setsar=1,fps=30,delogo=x=900:y=40:w=120:h=60,setpts=PTS-STARTPTS[tv1]", graph);
        Assert.Contains("[tv0][ta0][tv1][ta1]concat=n=2:v=1:a=1[top][voice0]", graph);
        Assert.Equal("7", list[list.IndexOf("-map") + 5]);
    }

    /// <summary>
    /// A wide or multi-person camera shot is shown whole - scaled to fit and letterboxed into the top
    /// half - instead of cropped tight, so the person speaking is never cut off. Chosen after a wide
    /// shot of the set's cartoon backdrop cropped onto the graphic and cut off the guest.
    /// </summary>
    [Fact]
    public void AWideShotIsLetterboxedWholeInsteadOfCroppedTight()
    {
        var graph = Graph(FfmpegArgumentBuilder.BuildPodcastSplit(Spec(top:
        [
            new FfmpegTopShot(0, 7, new PixelRect(0, 0, 1920, 1080), Fit: true),
        ])));

        Assert.Contains("crop=1920:1080:0:0,scale=1080:960:force_original_aspect_ratio=decrease,"
            + "pad=1080:960:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30", graph);
    }

    [Fact]
    public void FootageFillsTheBottomHalfForExactlyTheClipLength()
    {
        var args = FfmpegArgumentBuilder.BuildPodcastSplit(Spec());
        var graph = Graph(args);

        Assert.Contains("[2:v]crop=1215:1080:352:0,scale=1080:960,setsar=1,fps=30,tpad=stop_mode=clone:stop_duration=2.5,trim=duration=2.5,setpts=PTS-STARTPTS[bv0]", graph);
        Assert.Contains("[3:v]crop=810:720:100:0,scale=1080:960,setsar=1,fps=30,delogo=x=20:y=800:w=200:h=80,tpad=stop_mode=clone:stop_duration=4.5,trim=duration=4.5,setpts=PTS-STARTPTS[bv1]", graph);
        Assert.Contains("[bv0][bv1]concat=n=2:v=1:a=0[bottom]", graph);
        Assert.Contains("[top]pad=1080:1920:0:0:color=black[bg]", graph);
        Assert.Contains("[bg][bottom]overlay=0:960:eof_action=repeat,setpts=PTS-STARTPTS,subtitles=", graph);
        // vstack segfaults in the bundled FFmpeg, and the streamer effects belong to the other style.
        Assert.DoesNotContain("vstack", graph);
        Assert.DoesNotContain("zoompan", graph);
        Assert.DoesNotContain("colorchannelmixer", graph);
        Assert.DoesNotContain("adelay", graph);
        Assert.EndsWith("format=yuv420p,setsar=1[vout]", graph.Split(';').First(x => x.Contains("[vout]")));
    }

    [Fact]
    public void WatermarkSitsTopRightOfTheGuestHalfOverTheCaptions()
    {
        var logo = new FfmpegLogoOverlay(Path.Combine(Root, "logo.png"), "Top", 0.2, 0.05, 1, "222:150:0:850");
        var graph = Graph(FfmpegArgumentBuilder.BuildPodcastSplit(Spec(logo: logo)));

        Assert.Contains("[4:v]crop=222:150:0:850,scale=216:-1,format=rgba[vlogo]", graph);
        Assert.Contains("[vsplit][vlogo]overlay=W-w-W*0.074:H*0.071:format=auto,format=yuv420p,setsar=1[vout]", graph);
        Assert.True(graph.IndexOf("subtitles=", StringComparison.Ordinal) < graph.IndexOf("[vlogo]", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRequiredFootageIsRejectedInsteadOfDuplicatingTheCamera()
    {
        Assert.Throws<ArgumentException>(() => FfmpegArgumentBuilder.BuildPodcastSplit(Spec(broll: [])));
    }

    [Fact]
    public void MusicIsDuckedUnderTheCondensedVoice()
    {
        var graph = Graph(FfmpegArgumentBuilder.BuildPodcastSplit(Spec(music: Path.Combine(Root, "bed.mp3"))));

        Assert.Contains("[voice0]asplit=2[voice][voicekey]", graph);
        Assert.Contains("[4:a]atrim=0:7,asetpts=PTS-STARTPTS,volume=0.3[musicbase]", graph);
        Assert.Contains("[musicbase][voicekey]sidechaincompress=threshold=0.05:ratio=5:attack=20:release=500[musicducked]", graph);
        Assert.Contains("[voice][musicducked]amix=inputs=2:duration=first:dropout_transition=2,loudnorm=I=-14:TP=-1.5:LRA=11[aout]", graph);
    }

    /// <summary>
    /// The usable hook of a track is often minutes in - the guide puts one song's at 2:11 - so the
    /// song is seeked before it is read. Starting every bed at 0:00 would lay the intro under the clip.
    /// </summary>
    [Fact]
    public void MusicStartsWhereTheSegmentGuideMarksIt()
    {
        var song = Path.Combine(Root, "bed.mp3");

        var args = FfmpegArgumentBuilder.BuildPodcastSplit(Spec(music: song, musicStart: 131)).ToList();

        var input = args.LastIndexOf("-i");
        Assert.Equal(song, args[input + 1]);
        Assert.Equal(["-ss", "131", "-stream_loop", "-1", "-t", "7", "-i", song], args.Skip(input - 6).Take(8).ToArray());
    }

    /// <summary>A track the guide does not mention plays from its beginning, exactly as before.</summary>
    [Fact]
    public void ASongWithNoSegmentIsNotSeeked()
    {
        var args = FfmpegArgumentBuilder.BuildPodcastSplit(Spec(music: Path.Combine(Root, "bed.mp3"))).ToList();

        var input = args.LastIndexOf("-i");
        Assert.Equal(["-stream_loop", "-1", "-t", "7"], args.Skip(input - 4).Take(4).ToArray());
    }

    [Fact]
    public void RejectsASongThatStartsBeforeItsOwnBeginning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegArgumentBuilder.BuildPodcastSplit(Spec(music: Path.Combine(Root, "bed.mp3"), musicStart: -2)));
    }

    [Fact]
    public void RejectsACameraCropThatRunsOffTheSpan()
    {
        Assert.Throws<ArgumentException>(() => FfmpegArgumentBuilder.BuildPodcastSplit(
            Spec(top: [new FfmpegTopShot(0, 7, new PixelRect(1000, 0, 960, 853))])));
    }

    [Fact]
    public void RejectsFootageThatDoesNotCoverTheWholeClip()
    {
        Assert.Throws<ArgumentException>(() => FfmpegArgumentBuilder.BuildPodcastSplit(Spec(broll:
        [
            new FfmpegBrollShot(Path.Combine(Root, "a.mp4"), 0, 3, new PixelRect(352, 0, 1215, 1080), 1920, 1080),
        ])));
    }
}

public sealed class SeamCaptionTests
{
    private static TranscriptWord W(string word, double start, double end) => new(start, end, word);
    private static IEnumerable<string> Lines(string ass) =>
        ass.Split('\n').Where(x => x.StartsWith("Dialogue:", StringComparison.Ordinal));

    [Fact]
    public void EmphasisWordsAndNumbersAreItalicAndTheTagsSurviveEscaping()
    {
        var ass = CaptionBuilder.BuildSeamAss(
            [W("he", 0, 0.3), W("made", 0.3, 0.6), W("$15,000", 0.6, 1.2), W("crazy", 1.2, 1.6)], 2, ["Crazy"]);

        Assert.Contains(",HE\n", ass);
        Assert.Contains("{\\i1}$15,000{\\i0}", ass);
        Assert.Contains("{\\i1}CRAZY{\\i0}", ass);
        Assert.DoesNotContain("\\{\\i1", ass);
    }

    [Fact]
    public void AnEmphasisWordIsItalicOnlyTheFirstTimeItIsSaid()
    {
        var ass = CaptionBuilder.BuildSeamAss(
            [W("car", 0, 0.4), W("the", 0.4, 0.8), W("car", 0.8, 1.2), W("22", 1.2, 1.6), W("22", 1.6, 2)], 3, ["car"]);

        Assert.Single(Lines(ass), x => x.EndsWith("{\\i1}CAR{\\i0}", StringComparison.Ordinal));
        Assert.Single(Lines(ass), x => x.EndsWith(",CAR", StringComparison.Ordinal));
        Assert.Equal(2, Lines(ass).Count(x => x.EndsWith("{\\i1}22{\\i0}", StringComparison.Ordinal)));
    }

    [Fact]
    public void SwearingIsStarredButTheWordStaysReadable()
    {
        var ass = CaptionBuilder.BuildSeamAss([W("badass", 0, 0.5), W("fucking", 0.5, 1), W("class", 1, 1.5)], 2);

        Assert.Contains(",BAD***\n", ass);
        Assert.Contains(",F***ING\n", ass);
        Assert.Contains(",CLASS\n", ass);
    }

    [Fact]
    public void TonePunctuationStaysAndSentencePunctuationGoes()
    {
        var ass = CaptionBuilder.BuildSeamAss(
            [W("season!", 0, .4), W("up?", .4, .8), W("but...", .8, 1.2), W("done.", 1.2, 1.6), W("yeah,", 1.6, 2)], 3);

        Assert.Contains(",SEASON!\n", ass);
        Assert.Contains(",UP?\n", ass);
        Assert.Contains(",BUT...\n", ass);
        Assert.Contains(",DONE\n", ass);
        Assert.Contains(",YEAH\n", ass);
    }

    [Fact]
    public void ANumberAndItsUnitShareOneCaption()
    {
        var ass = CaptionBuilder.BuildSeamAss([W("$1.9", 0, 0.4), W("million", 0.45, 0.9)], 2);

        Assert.Single(Lines(ass));
        Assert.Contains("{\\i1}$1.9 MILLION{\\i0}", ass);
    }

    [Fact]
    public void KeptPiecesAreLaidEndToEndOnTheClipClock()
    {
        var clip = CaptionBuilder.RemapToClip(
            [W("one", 10.0, 10.4), W("two", 10.5, 10.9), W("cut", 30, 30.5), W("three", 50.2, 50.6)],
            [(10, 11), (50, 52)]);

        Assert.Equal(new[] { "one", "two", "three" }, clip.Select(x => x.Word));
        Assert.Equal(0, clip[0].Start, 3);
        Assert.Equal(0.5, clip[1].Start, 3);
        Assert.Equal(1.2, clip[2].Start, 3);
    }

    [Fact]
    public void TheWordSitsOnTheSplitLine()
    {
        var ass = CaptionBuilder.BuildSeamAss([W("hello", 0, 1)], 2);

        Assert.Contains("PlayResY: 1920", ass);
        Assert.Contains("Style: Caption,Segoe UI Black,84,&H00FFFFFF,&H000000FF,&H00000000,&H78000000,-1,0,0,0,100,100,0,0,1,4,3,5,60,60,0,1", ass);
    }
}

public sealed class PodcastSplitSupportTests
{
    private static TranscriptWord W(string word, double start, double end) => new(start, end, word);

    [Fact]
    public void ReadsYouTubeWordCaptionsAndSkipsMarkers()
    {
        const string json3 = """
            {"events":[
              {"tStartMs":1000,"dDurationMs":2000,"segs":[{"utf8":"hello"},{"utf8":" world","tOffsetMs":400}]},
              {"tStartMs":3000,"segs":[{"utf8":"\n"}]},
              {"tStartMs":5000,"segs":[{"utf8":"[Music]"},{"utf8":" >>","tOffsetMs":100},{"utf8":" again","tOffsetMs":200}]}
            ]}
            """;
        var transcript = YouTubeCaptions.ToTranscript(json3);

        Assert.Equal(new[] { "hello", "world", "again" }, transcript.Words.Select(x => x.Word));
        Assert.Equal(1.0, transcript.Words[0].Start, 3);
        Assert.Equal(1.4, transcript.Words[0].End, 3);
        // Capped: the next word is three seconds later.
        Assert.Equal(2.2, transcript.Words[1].End, 3);
        Assert.Equal(5.2, transcript.Words[2].Start, 3);
    }

    [Fact]
    public void AlignerFindsTheSectionOffsetDespiteMisheardWords()
    {
        var reference = Enumerable.Range(0, 60).Select(i => W($"word{i}", 100 + 0.3 * i, 100.25 + 0.3 * i)).ToList();
        var local = Enumerable.Range(0, 60)
            .Select(i => W(i is 10 or 20 or 30 ? "zzz" : $"word{i}", 2 + 0.3 * i, 2.25 + 0.3 * i)).ToList();

        var result = TranscriptAligner.EstimateOffset(reference, local);

        Assert.NotNull(result);
        Assert.Equal(98, result.Value.Offset, 2);
        Assert.True(result.Value.Matches >= 40);
    }

    [Fact]
    public void AlignerRefusesToGuessWithoutSharedPhrases()
    {
        var reference = Enumerable.Range(0, 30).Select(i => W($"word{i}", i, i + .2)).ToList();
        var local = Enumerable.Range(0, 30).Select(i => W($"other{i}", i, i + .2)).ToList();

        Assert.Null(TranscriptAligner.EstimateOffset(reference, local));
    }

    [Fact]
    public void PiecesSnapToSpokenWordsWithoutClippingANeighbour()
    {
        var words = new[]
        {
            W("so", 1.0, 1.2), W("the", 1.25, 1.4), W("thing", 1.45, 1.8), W("is", 1.9, 2.0),
            W("crazy", 2.05, 2.5), W("next", 2.52, 2.8),
        };

        var snapped = PieceSnapper.Snap([(1.3, 2.4)], words, 10);

        Assert.Single(snapped);
        Assert.Equal(1.2, snapped[0].Start, 3);
        Assert.Equal(2.52, snapped[0].End, 3);
    }

    [Fact]
    public void StoriesOutsideTheMeasuredLimitsAreDropped()
    {
        using var document = JsonDocument.Parse("""
            {"stories":[
              {"title":"Good one","rationale":"r","score":0.8,"emphasis":["Crazy"],
               "pieces":[{"startSeconds":100,"endSeconds":108},{"startSeconds":107,"endSeconds":112},{"startSeconds":140,"endSeconds":150}]},
              {"title":"Too short","rationale":"r","score":0.9,"emphasis":[],"pieces":[{"startSeconds":10,"endSeconds":15}]},
              {"title":"Too spread out","rationale":"r","score":0.7,"emphasis":[],
               "pieces":[{"startSeconds":0,"endSeconds":10},{"startSeconds":900,"endSeconds":910}]}
            ]}
            """);

        var stories = StoryParser.ParseStories(document.RootElement, 3000);

        var story = Assert.Single(stories);
        Assert.Equal("Good one", story.Title);
        Assert.Equal(new[] { (100d, 112d), (140d, 150d) }, story.Pieces);
        Assert.Equal(new[] { "crazy" }, story.Emphasis);
    }

    [Fact]
    public void BrollTimelineCoversTheClipExactlyAndChangesOnTheWords()
    {
        var slots = BrollTimeline.Plan(
            [new BrollMoment(0, 2, "a"), new BrollMoment(5, 7, "b"), new BrollMoment(5.3, 6, "c"), new BrollMoment(12, 14, "d")],
            20, 2.2);

        Assert.Equal(20, slots.Sum(x => x.DurationSeconds), 6);
        Assert.Equal(0, slots[0].AtSeconds);
        Assert.Equal(20, slots[^1].AtSeconds + slots[^1].DurationSeconds, 6);
        Assert.Equal(new[] { "a", "b", "d" }, slots.Select(x => x.Query).Distinct());
        Assert.Contains(slots, x => x.Query == "b" && Math.Abs(x.AtSeconds - 5) < 1e-6);
        Assert.All(slots, x => Assert.InRange(x.DurationSeconds, 1.5, 3.5));
    }

    [Fact]
    public void SearchSkipsTheChannelsOwnEditsShortsAndLiveStreams()
    {
        const string output = """
            {"id":"aaaaaaaaaaa","title":"Kobe Bryant game winner vs Bucks 2009","duration":412,"channel":"Hoops"}
            {"id":"bbbbbbbbbbb","title":"Kobe Bryant #shorts","duration":40,"channel":"Clips"}
            {"id":"ccccccccccc","title":"Kobe Bryant game winner story","duration":300,"channel":"Double Coverage TV"}
            {"id":"ddddddddddd","title":"LIVE Lakers","duration":null,"channel":"X","live_status":"is_live"}
            {"id":"eeeeeeeeeee","title":"Bucks highlights 2009","duration":600,"channel":"NBA"}
            {"id":"fffffffffff","title":"Kobe Bryant game winner - S04EP10 - Series Finale","duration":200,"channel":"HBO"}
            {"id":"ggggggggggg","title":"Kobe Bryant game winner movie scene","duration":300,"channel":"Clips"}
            {"id":"hhhhhhhhhhh","title":"Kobe Bryant game winner | Dhar Mann","duration":500,"channel":"Dhar Mann Studios"}
            not json
            """;

        var ranked = BrollCandidates.Rank(BrollCandidates.ParseSearch(output), "Kobe Bryant game winner Bucks 2009");

        Assert.Equal(new[] { "aaaaaaaaaaa" }, ranked.Select(x => x.Id));
    }

    [Fact]
    public void AResultMustShareWordsWithItsSearchAndFilmClipsAreOut()
    {
        const string output = """
            {"id":"aaaaaaaaaaa","title":"Man shadowboxing in the forest","duration":300,"channel":"Fit"}
            {"id":"bbbbbbbbbbb","title":"Teofimo Lopez vs Richard Commey knockout","duration":400,"channel":"Top Rank Boxing"}
            {"id":"ccccccccccc","title":"Rocky IV (1985) - Teofimo Lopez knockout training","duration":180,"channel":"Clips"}
            {"id":"ddddddddddd","title":"Teofimo Lopez knockout highlights","duration":250,"channel":"Movieclips"}
            {"id":"fffffffffff","title":"ROCKY | Teofimo Lopez knockout scene remake","duration":250,"channel":"Fan Edits"}
            {"id":"ggggggggggg","title":"Creed Gets Knocked Down, Teofimo Lopez style","duration":250,"channel":"Clips"}
            """;
        var named = BrollCandidates.Rank(BrollCandidates.ParseSearch(output), "Teofimo Lopez knockout highlights");
        var generic = BrollCandidates.Rank(BrollCandidates.ParseSearch(
            """{"id":"eeeeeeeeeee","title":"Forest walk 4K","duration":300,"channel":"Nature"}"""), "forest b roll");

        Assert.Equal(new[] { "bbbbbbbbbbb" }, named.Select(x => x.Id));
        Assert.Equal(new[] { "eeeeeeeeeee" }, generic.Select(x => x.Id));
    }

    [Fact]
    public void StructuredOutputIsUnwrappedFromClaudeAndCodexButNotFromEchoes()
    {
        const string plan = """{"moments":[{"startSeconds":0,"endSeconds":2,"query":"kobe game winner"}],"emphasis":[]}""";
        static bool IsPlan(JsonElement x) => x.TryGetProperty("moments", out _);
        using var payload = JsonDocument.Parse(plan);

        var claude = StructuredOutput.Extract(JsonSerializer.Serialize(new { type = "result", structured_output = payload.RootElement }), IsPlan);
        var codex = StructuredOutput.Extract(string.Join('\n',
            JsonSerializer.Serialize(new { type = "thread.started" }),
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = plan } })), IsPlan);
        var echoed = StructuredOutput.Extract(
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "command_execution", text = plan } }), IsPlan);

        Assert.NotNull(claude);
        Assert.NotNull(codex);
        Assert.Null(echoed);
        Assert.NotNull(StoryParser.ParseBrollPlan(claude!.Value, 2));
    }

    [Theory]
    [InlineData("Teofimo Lopez knockout highlights", "Mike Tyson knockout highlights")]
    [InlineData("Kobe Bryant game winner Bucks 2009", "Bucks highlights 2009")]
    [InlineData("Teofimo Lopez vs Richard Commey knockout", "Teofimo Lopez vs Josh Taylor knockout")]
    public void SharedFootageWordsNeverSubstituteForTheRequestedPersonOrEvent(string query, string wrongTitle)
    {
        var wrong = new BrollSearchEntry("aaaaaaaaaaa", wrongTitle, 300, "Sports", null, 0);
        Assert.Empty(BrollCandidates.Rank([wrong], query));
    }

    /// <summary>
    /// Plans describe the action, but videos are titled after people. Nine of nine Teofimo stories lost
    /// their footage to queries like these, so a search that comes back short is retried on the names.
    /// </summary>
    [Theory]
    [InlineData("Teofimo Lopez taking body punches fight highlights", "Teofimo Lopez highlights")]
    [InlineData("Rolando Romero overhand right boxing highlights", "Rolando Romero highlights")]
    [InlineData("Kobe Bryant game winner vs Bucks 2009", "Kobe Bryant Bucks 2009 highlights")]
    public void ADescribedSearchFallsBackToTheNamesInIt(string query, string expected)
    {
        Assert.Equal(expected, BrollCandidates.Simplify(query));
    }

    [Theory]
    [InlineData("luxury car dealership b roll")] // Already generic: there is nothing to strip.
    [InlineData("counting cash closeup")] // No name to search for.
    [InlineData("Teofimo Lopez highlights")] // Already the names alone.
    public void AQueryWithNothingToStripIsNotRetried(string query)
    {
        Assert.Null(BrollCandidates.Simplify(query));
    }

    [Fact]
    public void TheNamesAloneMatchTheFootageTheDescribedQueryMissed()
    {
        var entry = new BrollSearchEntry("bbbbbbbbbbb", "8 MINUTES OF TEOFIMO LOPEZ HIGHLIGHTS", 480, "Boxing", null, 0);
        const string described = "Teofimo Lopez pull counter defensive boxing highlights";

        Assert.Empty(BrollCandidates.Rank([entry], described));
        Assert.Single(BrollCandidates.Rank([entry], BrollCandidates.Simplify(described)!));
    }

    [Theory]
    [InlineData(1, 10)] // Missing opening.
    [InlineData(0, 9)]  // Missing ending.
    public void PartialSemanticPlansAreRejected(double start, double end)
    {
        var root = JsonSerializer.SerializeToElement(new
        {
            moments = new[] { new { startSeconds = start, endSeconds = end, query = "Teofimo Lopez highlights" } },
            emphasis = Array.Empty<string>()
        });
        Assert.Null(StoryParser.ParseBrollPlan(root, 10));
    }

    [Fact]
    public void MissingSemanticMiddleCannotBeFilledByCarryingThePreviousSubject()
    {
        using var root = JsonDocument.Parse("""
            {"moments":[
              {"startSeconds":0,"endSeconds":3,"query":"Teofimo Lopez highlights"},
              {"startSeconds":5,"endSeconds":10,"query":"Ryan Garcia highlights"}
            ],"emphasis":[]}
            """);
        Assert.Null(StoryParser.ParseBrollPlan(root.RootElement, 10));
    }
}

public sealed class BrollCleanAllocatorTests
{
    [Fact]
    public void ASlotSpansSeveralCleanIntervalsWithoutReadingTheExcludedFramesBetweenThem()
    {
        var allocation = BrollCleanAllocator.Allocate([new BrollSlot(0, 2.2, 0, "Teofimo Lopez")],
            [new BrollCleanStretch(0, 0.5, 1.3), new BrollCleanStretch(1, 4, 3)]);

        Assert.Empty(allocation.Gaps);
        Assert.Equal(2, allocation.Takes.Count);
        Assert.Equal(1.3, allocation.Takes[0].DurationSeconds, 6);
        Assert.Equal(4, allocation.Takes[1].InSeconds, 6);
        Assert.Equal(0.9, allocation.Takes[1].DurationSeconds, 6);
        Assert.Equal(2.2, allocation.Takes.Sum(x => x.DurationSeconds), 6);
    }

    [Fact]
    public void InsufficientFootageRemainsAnExplicitGapAndBlocksRendering()
    {
        var allocation = BrollCleanAllocator.Allocate([new BrollSlot(5, 2.2, 1, "Ryan Garcia")],
            [new BrollCleanStretch(0, 0.5, 1.3)]);

        var gap = Assert.Single(allocation.Gaps);
        Assert.Equal(6.3, gap.AtSeconds, 6);
        Assert.Equal(0.9, gap.DurationSeconds, 6);
        Assert.Equal("Ryan Garcia", gap.Query);
        Assert.Throws<InvalidOperationException>(() => BrollCleanAllocator.RequireComplete(allocation.Takes.Count,
            allocation.Gaps.Sum(x => x.DurationSeconds), clipSeconds: 2));
    }

    /// <summary>
    /// Gaps nothing could be found for hold the neighbouring shot instead of losing the clip, which is
    /// what Matthew asked for after whole stories failed on short gaps. The limit is a third of the clip:
    /// a talk-show moment with a few unmatched seconds renders, but a clip that is mostly unmatched fails.
    /// </summary>
    [Fact]
    public void FootageGapsUpToAThirdOfTheClipAreHeldButMoreFails()
    {
        BrollCleanAllocator.RequireComplete(shotCount: 8, gapSeconds: 4, clipSeconds: 30);
        BrollCleanAllocator.RequireComplete(shotCount: 8, gapSeconds: 10, clipSeconds: 30);

        Assert.Throws<InvalidOperationException>(() => BrollCleanAllocator.RequireComplete(8, 13, 30));
    }

    [Fact]
    public void ConsecutiveSlotsConsumeEachCleanFrameAtMostOnce()
    {
        var allocation = BrollCleanAllocator.Allocate(
            [new BrollSlot(0, 2.2, 0, "a"), new BrollSlot(2.2, 2.2, 0, "a")],
            [new BrollCleanStretch(0, 1, 3), new BrollCleanStretch(1, 6, 2)]);

        Assert.Empty(allocation.Gaps);
        Assert.Equal(4.4, allocation.Takes.Sum(x => x.DurationSeconds), 6);
        Assert.All(allocation.Takes.Where(x => x.StretchIndex == 0), x => Assert.True(x.InSeconds + x.DurationSeconds <= 4.000001));
        Assert.All(allocation.Takes.Where(x => x.StretchIndex == 1), x => Assert.True(x.InSeconds + x.DurationSeconds <= 8.000001));
        Assert.Equal(3.2, allocation.Takes[1].InSeconds, 6);
    }

    [Fact]
    public void TinyCleanRemaindersDoNotCreateInvalidSubFrameShots()
    {
        var allocation = BrollCleanAllocator.Allocate([new BrollSlot(0, 2.2, 0, "a")],
            [new BrollCleanStretch(0, 0, 2.18), new BrollCleanStretch(1, 5, 1)]);

        Assert.Empty(allocation.Gaps);
        Assert.All(allocation.Takes, x => Assert.True(x.DurationSeconds >= 0.059999));
        Assert.Equal(2.2, allocation.Takes.Sum(x => x.DurationSeconds), 6);
        Assert.True(allocation.Takes[0].InSeconds + allocation.Takes[0].DurationSeconds <= 2.18);
    }

    [Fact]
    public void EmptyFootageCannotPassEvenWithAnEmptyGapList()
    {
        Assert.Throws<InvalidOperationException>(() => BrollCleanAllocator.RequireComplete(0, 0, 20));
        BrollCleanAllocator.RequireComplete(1, 0, 20);
    }
}
