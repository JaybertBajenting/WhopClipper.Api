using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests.Tests;

public sealed class NarratedTests
{
    private static List<TranscriptWord> Words(params (double Start, double End, string Word)[] words) =>
        words.Select(x => new TranscriptWord(x.Start, x.End, x.Word)).ToList();

    [Fact]
    public void ElevenLabsCharacterTimingsBecomeWords()
    {
        using var document = JsonDocument.Parse("""
            {"characters":["T","h","i","s"," ","$","1","8","4",","," ","t","r","a","d","e","r","."],
             "character_start_times_seconds":[0,0.05,0.1,0.15,0.2,0.25,0.3,0.35,0.4,0.45,0.5,0.55,0.6,0.65,0.7,0.75,0.8,0.85],
             "character_end_times_seconds":[0.05,0.1,0.15,0.2,0.25,0.3,0.35,0.4,0.45,0.5,0.55,0.6,0.65,0.7,0.75,0.8,0.85,0.9]}
            """);
        var words = ElevenLabsClient.WordsFromAlignment(document.RootElement);
        Assert.Equal(["This", "$184,", "trader."], words.Select(x => x.Word));
        Assert.Equal((0.25, 0.5), (words[1].Start, words[1].End));
        Assert.Equal(0.9, words[^1].End);
    }

    [Fact]
    public void TheFirstShotHoldsThroughTheOpeningAndCutsLandOnWords()
    {
        // 9 s of narration, a word every 0.45 s.
        var narration = Enumerable.Range(0, 20).Select(i => new TranscriptWord(0.1 + i * 0.45, 0.1 + i * 0.45 + 0.35, $"w{i}")).ToList();
        var slots = NarratedStories.Allocate(9, narration, [3, 3, 3, 3]);

        Assert.Equal(1.75, slots[0].DurationSeconds);
        Assert.Equal(9, slots.Sum(x => x.DurationSeconds), 3);
        var clock = 0d;
        foreach (var slot in slots.Take(slots.Count - 1))
        {
            clock += slot.DurationSeconds;
            if (clock > 1.8) Assert.Contains(narration, w => Math.Abs(w.Start - clock) < 0.001);
        }
        Assert.All(slots, x => Assert.InRange(x.DurationSeconds, 0.6, 2.2));
        // Four candidates where the narration has room for seven shots: the shots run longer, so only five are
        // needed, and the one too many shows the first candidate again from a later point.
        Assert.Equal([0, 1, 2, 3, 0], slots.Select(x => x.Candidate));
        Assert.Equal((0, 1.2), (slots[4].Candidate, slots[4].InSeconds));
    }

    /// <summary>His words: "the videos in the background keep repeating, making the viewer lost interest". A clip
    /// about a Urus showed one steering wheel three times under its narrator.</summary>
    [Fact]
    public void NoShotPlaysTwiceWhileAnotherIsLeft()
    {
        var narration = Enumerable.Range(0, 20).Select(i => new TranscriptWord(0.1 + i * 0.45, 0.1 + i * 0.45 + 0.35, $"w{i}")).ToList();
        // Enough candidates: seven shots, each its own, cut about every 1.2 s as the reference does.
        var plenty = NarratedStories.Allocate(9, narration, [4, 4, 4, 4, 4, 4, 4, 4]);
        Assert.Equal(7, plenty.Count);
        Assert.Equal(7, plenty.Select(x => x.Candidate).Distinct().Count());
        // Five candidates: five shots of about 1.8 s, and still none twice.
        var few = NarratedStories.Allocate(9, narration, [4, 4, 4, 4, 4]);
        Assert.Equal([0, 1, 2, 3, 4], few.Select(x => x.Candidate));
        Assert.All(few, x => Assert.InRange(x.DurationSeconds, 1.4, 2.3));
        // A candidate whose strong part is two seconds cannot be held that long, so the shots stay shorter and one
        // long candidate comes round again rather than the short one running into what was left out of it.
        var halves = NarratedStories.Allocate(9, narration, [4, 2, 4, 4]);
        Assert.All(halves, x => Assert.InRange(x.DurationSeconds, 0.6, 2.0));
        Assert.Equal(1, halves.Count(x => x.Candidate == 1));
        // In a car clip the car shots fill first, then a shot without a car, and only then a car shot again. What
        // the look at the pictures asked for the shots that were merged still plays, each once.
        var cars = NarratedStories.Allocate(9, narration, [4, 4, 4, 4, 4], asked: [null, 4, null, 2, 2, null, 3], fill: [2, 3, 4]);
        Assert.Equal(1, cars.Count(x => x.Candidate == 2));
        Assert.Equal(cars.Count, cars.Select(x => x.Candidate).Distinct().Count());
        Assert.Equal(4, cars[1].Candidate);
    }

    [Fact]
    public void AShortNarrationIsOneShot()
    {
        var slots = NarratedStories.Allocate(2.1, Words((0.1, 0.5, "This"), (0.5, 1.9, "trader")), [3]);
        Assert.Equal(2.1, slots.Sum(x => x.DurationSeconds), 3);
        Assert.True(slots.Count <= 2);
    }

    /// <summary>"Trader Gives Stranger A Free Hellcat" never showed the Hellcat: the shots were dealt out in the order
    /// they were found, so the word "Hellcat" played over a face.</summary>
    [Fact]
    public void AShotShowsWhatTheNarratorIsSaying()
    {
        // Six shots over five candidates. The look at the pictures matched the third shot to candidate 4 and the
        // fifth to candidate 1; the rest take candidates not yet shown, leaving those a later shot asks for. Six
        // shots over five candidates: only the last has to show one again.
        Assert.Equal([0, 2, 4, 3, 1, 0], NarratedStories.Arrange(6, 5, [null, null, 4, null, 1, null]));
        // Nothing asked: the candidates in order, and round again only when all have been shown.
        Assert.Equal([0, 1, 2, 0, 1], NarratedStories.Arrange(5, 3));
        // The first shot is the opening shot whatever was asked for it.
        Assert.Equal(0, NarratedStories.Arrange(3, 4, [3, null, null])[0]);
        // One candidate asked for again and again plays once; the shots left over go round the others.
        Assert.Equal([0, 2, 1, 0, 1], NarratedStories.Arrange(5, 3, [null, 2, 2, 2, 2]));
        // A label that is not a candidate is a shot nothing was asked for.
        Assert.Equal([0, 1, 2], NarratedStories.Arrange(3, 3, [null, 9, -1]));
        // In a car clip the shots nothing was asked for are filled from the car shots first; a shot without a car
        // comes before a car shot shown a second time.
        Assert.Equal([0, 2, 3, 1, 2], NarratedStories.Arrange(5, 4, null, [2, 3]));
        // A shot without a car still plays where it was asked for: the money, the stranger's face.
        Assert.Equal([0, 1, 2, 3, 2], NarratedStories.Arrange(5, 4, [null, 1, null, null, null], [2, 3]));
        Assert.Empty(NarratedStories.Arrange(0, 3));

        var narration = Enumerable.Range(0, 20).Select(i => new TranscriptWord(0.1 + i * 0.45, 0.1 + i * 0.45 + 0.35, $"w{i}")).ToList();
        var slots = NarratedStories.Allocate(9, narration, [4, 4, 4, 4], asked: [null, 3, null, 3, null, null, null]);
        Assert.Equal((3, 0d), (slots[1].Candidate, slots[1].InSeconds));
        // Asked for twice, it still plays once. The one shot too many shows the opener again, from further in.
        Assert.Equal(1, slots.Count(x => x.Candidate == 3));
        Assert.Equal((0, NarratedStories.ReuseStepSeconds), (slots[^1].Candidate, slots[^1].InSeconds));
    }

    /// <summary>His words: "dont include the shots that are weak". The re-cut Hellcat clip had one: a man beside the
    /// car with only a corner of it in the frame.</summary>
    [Fact]
    public void AWeakShotIsLeftOutWhileThereAreEnoughStrongOnes()
    {
        Assert.Equal(["wheel", "handover", "close-up"], NarratedStories.WithoutWeak<string>(
            [("wheel", false), ("a corner of the car", true), ("handover", false), ("close-up", false), ("a stranger", true)]));
        // Two strong shots cannot carry a montage: the weak ones come back, after the strong ones.
        Assert.Equal(["wheel", "handover", "a corner of the car"], NarratedStories.WithoutWeak<string>(
            [("a corner of the car", true), ("wheel", false), ("handover", false)]));
        Assert.Empty(NarratedStories.WithoutWeak<string>([]));
    }

    /// <summary>A candidate once showed a grey Lamborghini while the narrator said "Hellcat": its four seconds
    /// straddled one of the vlog's own cuts, and only its last frame was the Hellcat.</summary>
    [Fact]
    public void ACandidateThatStraddlesACutIsCutDownToOneShot()
    {
        Assert.Equal((0d, 4d), NarratedStories.LongestShot(4, []));
        // A cut at 2.9 s: the first shot, kept a tenth of a second clear of the cut.
        Assert.Equal((0d, 2.8), NarratedStories.LongestShot(4, [2.9]));
        // A cut at 0.8 s: the tail of another shot goes, and the rest starts a tenth after the cut.
        Assert.Equal((0.9, 3.1), NarratedStories.LongestShot(4, [0.8]));
        // Three shots: the longest, clear of the cut on either side.
        Assert.Equal((1.1, 2.3), NarratedStories.LongestShot(4, [3.5, 1.0]));
        // Two shots alike: the earlier. Cuts outside the file are ignored.
        Assert.Equal((0d, 1.9), NarratedStories.LongestShot(4, [2.0, 4.0, -1]));
    }

    /// <summary>Phone footage dropped into a vlog sits between black bars, and a window centred on a car near the
    /// picture's edge showed a bar.</summary>
    [Fact]
    public void TheWindowStaysInsideAPictureThatDoesNotFillTheFrame()
    {
        var half = NarratedStories.WindowShare / 2;
        // A full frame: the window goes where the subject is, as far as the frame's own edge.
        Assert.Equal(0.6, NarratedStories.KeepInPicture(0.6, 0, 1));
        Assert.Equal(Math.Round(1 - half, 4), NarratedStories.KeepInPicture(0.9, 0, 1));
        // A picture from 0.2 to 0.8: the window stops at its edges.
        Assert.Equal(Math.Round(0.8 - half, 4), NarratedStories.KeepInPicture(0.75, 0.2, 0.8));
        Assert.Equal(Math.Round(0.2 + half, 4), NarratedStories.KeepInPicture(0.1, 0.2, 0.8));
        Assert.Equal(0.5, NarratedStories.KeepInPicture(0.5, 0.2, 0.8));
        // Narrower than the window, it is centred; such a candidate is dropped before it gets here.
        Assert.Equal(0.5, NarratedStories.KeepInPicture(0.9, 0.35, 0.65));
    }

    /// <summary>Four seconds of vlog that start on the car's bonnet and swing to two men hugging: the second time the
    /// shot played, it showed a blur with a corner of the car.</summary>
    [Fact]
    public void OnlyTheStrongPartOfAShotPlays()
    {
        // The three frames of a 4 s candidate are taken at 0.3, 2.0 and 3.6 s.
        Assert.Equal([0.3, 2.0, 3.6], NarratedStories.ReviewFrameTimes(4));
        Assert.Equal((0d, 4d), NarratedStories.StrongPart(4, []));
        // What lies between a strong frame and a weak one was never looked at, so the part kept stops at the last
        // strong frame, or starts at the first.
        Assert.Equal((0d, 2d), NarratedStories.StrongPart(4, [3]));
        Assert.Equal((2d, 2d), NarratedStories.StrongPart(4, [1]));
        // One strong frame with a weak one on either side, or at either end, is not a shot.
        Assert.Null(NarratedStories.StrongPart(4, [2]));
        Assert.Null(NarratedStories.StrongPart(4, [2, 3]));
        Assert.Null(NarratedStories.StrongPart(4, [1, 2]));
        Assert.Null(NarratedStories.StrongPart(4, [1, 2, 3]));
        // A download that came short has too little left to show.
        Assert.Null(NarratedStories.StrongPart(1.6, [3]));

        using var document = JsonDocument.Parse("""[{"weakFrames":[2,3,3,7,"1"]},{"weakFrames":[]},{}]""");
        var rows = document.RootElement.EnumerateArray().Select(NarratedStories.ParseWeakFrames).ToList();
        Assert.Equal([2, 3], rows[0].Order());
        Assert.Empty(rows[1]);
        Assert.Empty(rows[2]);

        // A shot with one short strong part never comes round again; a longer one does, when all have been shown.
        Assert.Equal([0, 1, 2, 3, 0], NarratedStories.Arrange(5, 4, [null, 1, 2, 1, 2], most: [2, 1, 2, 2]));
        var narration = Enumerable.Range(0, 20).Select(i => new TranscriptWord(0.1 + i * 0.45, 0.1 + i * 0.45 + 0.35, $"w{i}")).ToList();
        var slots = NarratedStories.Allocate(9, narration, [4, 2, 4, 4], asked: [null, 1, 2, 1, 2, null, null]);
        Assert.Equal(1, slots.Count(x => x.Candidate == 1));
    }

    [Fact]
    public void EachShotKnowsTheWordsSpokenOverIt()
    {
        var words = Words((0.1, 0.4, "This"), (0.4, 0.9, "trader"), (1.8, 2.2, "gave"), (2.2, 2.4, "a"), (2.4, 3.0, "stranger"),
            (3.1, 3.3, "a"), (3.3, 3.9, "Hellcat."));
        Assert.Equal(["This trader", "gave a stranger", "a Hellcat."], NarratedStories.Beats([0, 1.75, 3.1, 4.2], words));
        // The cuts are the ones the shots are laid on, when there is a candidate for each.
        var narration = Enumerable.Range(0, 20).Select(i => new TranscriptWord(0.1 + i * 0.45, 0.1 + i * 0.45 + 0.35, $"w{i}")).ToList();
        var cuts = NarratedStories.Cuts(9, narration);
        Assert.Equal(NarratedStories.Allocate(9, narration, [4, 4, 4, 4, 4, 4, 4, 4]).Count, cuts.Count - 1);
        Assert.Equal((0d, 1.75, 9d), (cuts[0], cuts[1], cuts[^1]));
        Assert.Equal(20, NarratedStories.Beats(cuts, narration).Sum(x => x.Split(' ').Length));
    }

    [Fact]
    public void ThePictureLeavesTheSpeakerOnlyInTheMiddleOfTheMoment()
    {
        // An 8 s moment with a word every 0.4 s: two stretches, each starting and ending on a word, the first and
        // last second and a half left on the speaker.
        var windows = NarratedStories.CutawayWindows(8, Enumerable.Range(0, 20).Select(i => i * 0.4).ToList());
        Assert.Equal(2, windows.Count);
        Assert.Equal((1.6, 1.6), (windows[0].Start, windows[0].Seconds));
        Assert.Equal((4.8, 1.6), (windows[1].Start, windows[1].Seconds));
        Assert.All(windows, x => Assert.True(x.Start >= 1.5 - 0.35 && x.Start + x.Seconds <= 6.5));
        // Too short to leave him at all.
        Assert.Empty(NarratedStories.CutawayWindows(3.4, []));
        // A long moment is not turned into a slideshow: he is on screen between every two.
        var many = NarratedStories.CutawayWindows(40, []);
        Assert.Equal(6, many.Count);
        Assert.All(many.Zip(many.Skip(1)), x => Assert.True(x.Second.Start - (x.First.Start + x.First.Seconds) >= 1.5 - 0.001));
    }

    [Fact]
    public void ASlotTheReviewLeftOpenIsNotChosen()
    {
        using var document = JsonDocument.Parse(
            """{"slots":[{"slot":"S2","label":"M4"},{"slot":"C1","label":null},{"slot":" s3 ","label":" M1 "},{"slot":"S2","label":"M9"}]}""");
        var slots = NarratedStories.ParseSlots(document.RootElement);
        Assert.Equal(2, slots.Count);
        Assert.Equal("M4", slots["S2"]);
        Assert.Equal("M1", slots["S3"]);
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(NarratedStories.ParseSlots(empty.RootElement));
    }

    [Fact]
    public void ACutawayCoversTheWindowWhileTheMomentKeepsTalking()
    {
        FfmpegNarratedShot[] shots = [new("m1.mp4", 0, 3, 0.5, false), new("span.mp4", 3, 8, 0.5, true)];
        var spec = new FfmpegNarratedSpec(shots, "narration.mp3", 3, "out.mp4", 30, 20, new FfmpegNarratedImage("title.png", 1080, 70),
            Cutaways: [new("car.mp4", 1.2, 4.6, 1.6, 0.7)]);
        var args = FfmpegArgumentBuilder.BuildNarrated(spec).ToList();
        var graph = args[args.IndexOf("-filter_complex") + 1];

        // Inputs: two shots, the narration, the title, then the cutaway, read from where the montage left its file.
        Assert.Equal(["-ss", "1.2", "-t", "2.1", "-i", "car.mp4"], args.Skip(args.IndexOf("car.mp4") - 5).Take(6));
        Assert.Contains($"[4:v]scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,setsar=1,crop=1016:1080:{FfmpegArgumentBuilder.NarratedCropX(0.7)}:0", graph);
        // On the clip's clock, over the window only, and gone the moment it ends.
        Assert.Contains("setpts=PTS-STARTPTS+4.6/TB[cut0]", graph);
        Assert.Contains("[pic][cut0]overlay=x=0:y=361:eof_action=pass:enable='between(t\\,4.6\\,6.2)'[cutpic0]", graph);
        // Captions, the freeze and the title are drawn after it, so they stay on top.
        Assert.Contains("[cutpic0]tpad=stop_mode=clone", graph);
        // Its own sound is never used: the moment's keeps playing underneath.
        Assert.DoesNotContain("[4:a]", graph);
        Assert.Contains("[1:a]", graph);

        var plain = FfmpegArgumentBuilder.BuildNarrated(spec with { Cutaways = null }).ToList();
        Assert.Contains("[pic]tpad=stop_mode=clone", plain[plain.IndexOf("-filter_complex") + 1]);
        Assert.DoesNotContain("cut0", plain[plain.IndexOf("-filter_complex") + 1]);
        // Never over the narrator's montage, and never past the last word, where the clip freezes on the speaker.
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgumentBuilder.BuildNarrated(spec with { Cutaways = [new("car.mp4", 0, 2, 1.6, 0.5)] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgumentBuilder.BuildNarrated(spec with { Cutaways = [new("car.mp4", 0, 10, 1.6, 0.5)] }));
    }

    [Fact]
    public void AClipMadeBeforeCutawaysHasNone()
    {
        const string stored = """
            {"spanRelativePath":"span.mp4","spanWidth":1920,"spanHeight":1080,"pieces":[],"shots":[],"narration":"n",
             "narrationRelativePath":"narration.mp3","narrationSeconds":3,"words":[],"overlayTitle":"t","title":"t",
             "arrow":null,"skull":null,"episodeOffsetSeconds":0,"voice":"v"}
            """;
        var plan = JsonDefaults.Parse<NarratedPlanDto?>(stored, null)!;
        Assert.Null(plan.Cutaways);
        var cut = new NarratedCutawayDto("montage-04.mp4", 1.2, 1.6, 1.6, 0.7, "the purple Hellcat", 1400);
        var again = JsonDefaults.Parse<NarratedPlanDto?>(JsonSerializer.Serialize(plan with { Cutaways = [cut] }, JsonDefaults.Serializer), null)!;
        Assert.Equal(cut, Assert.Single(again.Cutaways!));
    }

    [Fact]
    public void StoriesNeedNarrationAMemeLineAndMontageOutsideTheMoment()
    {
        var transcript = Enumerable.Range(0, 400).Select(i => new TranscriptWord(i * 0.5, i * 0.5 + 0.4, $"word{i}")).ToList();
        var narration = "This 24 year old trader made $184,000 on one trade while on vacation in Greece, and what he did next is crazy.";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            stories = new object[]
            {
                new
                {
                    title = "TJR's $184K trade in Greece 💀", caption = "TJR made $184K on vacation", overlayTitle = "POV: Your office is Greece 💀",
                    narration, teller = "TJR", emphasis = new[] { "$184,000" }, score = 0.9, rationale = "",
                    pieces = new[] { new { startSeconds = 100.0, endSeconds = 116.0, firstWords = "", lastWords = "" } },
                    montage = new[] { new { atSeconds = 20.0, shows = "TJR on the yacht" }, new { atSeconds = 105.0, shows = "inside the moment" },
                        new { atSeconds = 150.0, shows = "the villa" } },
                },
                new
                {
                    title = "Too short narration", caption = "", overlayTitle = "POV: short 💀", narration = "This trader won.",
                    teller = "TJR", emphasis = Array.Empty<string>(), score = 0.8, rationale = "",
                    pieces = new[] { new { startSeconds = 30.0, endSeconds = 45.0, firstWords = "", lastWords = "" } },
                    montage = new[] { new { atSeconds = 90.0, shows = "a" }, new { atSeconds = 150.0, shows = "b" } },
                },
            },
        }));
        var stories = NarratedStories.Parse(document.RootElement, 200, transcript);

        var story = Assert.Single(stories);
        Assert.Equal(narration, story.Narration);
        Assert.Equal("POV: Your office is Greece 💀", story.OverlayTitle);
        // The montage moment inside the moment's own stretch is dropped.
        Assert.Equal([20d, 150d], story.Montage.Select(x => x.AtSeconds));
    }

    [Fact]
    public void NarrationIsPlainSpeech()
    {
        Assert.Equal("This trader, who never loses, made it", NarratedStories.CleanNarration("This trader — who “never” loses -\n made it"));
        Assert.Equal("He did it, again", NarratedStories.CleanNarration("He did it - again"));
    }

    [Fact]
    public void CaptionsAreOneColouredWordAtATimeAboveTheWindowCentre()
    {
        var ass = CaptionBuilder.BuildNarratedAss(Words((0, 0.4, "This"), (0.4, 0.9, "millionaire,"), (0.9, 1.3, "made"),
            (1.3, 1.9, "$184,000."), (5, 5.4, "wow")), 5.2);
        var lines = ass.Split('\n').Where(x => x.StartsWith("Dialogue:")).ToList();
        Assert.Equal(5, lines.Count);
        Assert.All(lines, x => Assert.Contains($"\\pos(540,{CaptionBuilder.NarratedCaptionY})", x));
        Assert.Contains("\\1c&HF2E83C&}millionaire", lines[1]);
        Assert.EndsWith("}$184,000", lines[3].TrimEnd());
        // A word before a long pause does not sit on screen through it, and nothing runs past the clip.
        Assert.Contains("0:00:01.30,0:00:02.25", lines[3]);
        Assert.Contains(",0:00:05.20,", lines[4]);
    }

    [Fact]
    public void TheWindowKeepsTheSubjectInViewWithoutRunningOffTheFrame()
    {
        Assert.Equal(452, FfmpegArgumentBuilder.NarratedCropX(0.5));
        Assert.Equal(0, FfmpegArgumentBuilder.NarratedCropX(0.05));
        Assert.Equal(1920 - FfmpegArgumentBuilder.NarratedVisibleWidth, FfmpegArgumentBuilder.NarratedCropX(0.99));
        Assert.Equal(452, FfmpegArgumentBuilder.NarratedCropX(double.NaN));
        Assert.Equal(361, FfmpegArgumentBuilder.NarratedWindowTop);
    }

    [Fact]
    public void TheRenderGraphOpensWideThenPlaysTheMomentWithItsOwnSound()
    {
        var spec = new FfmpegNarratedSpec(
            [
                new FfmpegNarratedShot("m1.mp4", 0, 1.75, 0.4, false), new FfmpegNarratedShot("m2.mp4", 0.2, 1.25, 0.6, false),
                new FfmpegNarratedShot("span.mp4", 3, 6, 0.5, true), new FfmpegNarratedShot("span.mp4", 12, 5, 0.5, true),
            ],
            "narration.mp3", 3, "out.mp4", 60, 20, new FfmpegNarratedImage("title.png", 1080, 70), "captions.ass",
            new FfmpegNarratedImage("arrow.png", 300, 272), 249, 59, new FfmpegNarratedMark(0.45, 0.35, 0.08),
            new FfmpegNarratedImage("skull.png", 437, 422), new FfmpegNarratedMark(0.5, 0.4, 0.1), "song.mp3", 12, 0.5);
        var args = FfmpegArgumentBuilder.BuildNarrated(spec);
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("scale=w='2*trunc(960*", graph);
        Assert.Contains("eval=frame", graph);
        Assert.Contains("pad=1080:1920:0:361:color=black[v1]", graph);
        Assert.Contains("concat=n=4:v=1:a=0[pic]", graph);
        // Only the moment's pieces give sound; the montage is silent under the narrator.
        Assert.Contains("[2:a]", graph);
        Assert.Contains("[3:a]", graph);
        Assert.DoesNotContain("[0:a]", graph);
        Assert.DoesNotContain("[1:a]", graph);
        Assert.Contains("hue=s=0:enable='gte(t\\,14)'", graph);
        Assert.Contains("enable='lt(t\\,0.9)'", graph);
        // The freeze is the 0.6 s the reference holds under the drop.
        Assert.Equal("14.6", args[args.ToList().IndexOf("-t", args.ToList().IndexOf("-filter_complex")) + 1]);
        // The song keeps its own dynamics (quiet build, loud drop) and is never normalised: low while anyone speaks,
        // then up to its full gain the moment the freeze starts.
        Assert.Contains("atrim=start=12:duration=14.6,asetpts=PTS-STARTPTS,volume=volume='if(lt(t\\,14)\\,0.2\\,0.5)':eval=frame[music]", graph);
        Assert.DoesNotContain("loudnorm=I=-14:TP=-2", graph);
        Assert.DoesNotContain("sidechaincompress", graph);
    }

    [Fact]
    public void TheLastWordLandsOnTheSongsDrop()
    {
        // Canto de Luna (super slowed): rises at 4.35, 10.65, 53.35 and 85.35 s; the reference's clips end at 0:54.
        MusicDrop[] drops = [new(4.35, 15.6), new(10.65, 12.6), new(53.35, 19.5, -4.5), new(85.35, 24.3)];
        // A 19.2 s clip starts the song at 34.15 s, so the freeze begins exactly at the 53.35 s drop.
        Assert.Equal(34.15, NarratedStories.MusicStartForDrop(drops, 19.2, 54, 0.6));
        // Too long to fit before that drop: the next one is used.
        Assert.Equal(25.35, NarratedStories.MusicStartForDrop(drops, 60, 54, 0.6));
        // Without a guide the biggest rise late enough wins.
        Assert.Equal(66.15, NarratedStories.MusicStartForDrop(drops, 19.2, null, 0.6));
        // The ultra slowed version has smaller rises near its big one; the big one near the guide's end is chosen.
        Assert.Equal(32.95, NarratedStories.MusicStartForDrop(
            [new(40.85, 10.4), new(49.6, 13.1), new(52.95, 17.2, -10.5), new(93.05, 15.1)], 20, 60, 0.6));
        // No drop late enough, or none at all: the caller falls back to the guide's start.
        Assert.Null(NarratedStories.MusicStartForDrop(drops, 90, 54, 0.6));
        Assert.Null(NarratedStories.MusicStartForDrop([], 20, 54, 0.6));
    }

    [Fact]
    public void EverySongsDropPlaysAtTheSameLoudness()
    {
        // One version of Canto de Luna sits at -4.5 dB after its drop and another at -10.5; both land at -12.
        Assert.Equal(0.422, NarratedStories.DropGain(-4.5, 0.4));
        Assert.Equal(0.841, NarratedStories.DropGain(-10.5, 0.4));
        Assert.Equal(-12, -4.5 + 20 * Math.Log10(NarratedStories.DropGain(-4.5, 0.4)), 1);
        Assert.Equal(-12, -10.5 + 20 * Math.Log10(NarratedStories.DropGain(-10.5, 0.4)), 1);
        // The campaign's slider scales it, within sane limits.
        Assert.Equal(0.211, NarratedStories.DropGain(-4.5, 0.2));
        Assert.Equal(1.5, NarratedStories.DropGain(-30, 0.4));

        var spec = new FfmpegNarratedSpec(
            [new FfmpegNarratedShot("m1.mp4", 0, 3, 0.5, false), new FfmpegNarratedShot("span.mp4", 3, 11, 0.5, true)],
            "narration.mp3", 3, "out.mp4", 30, 20, new FfmpegNarratedImage("title.png", 1080, 70), MusicPath: "song.mp3",
            MusicStartSeconds: 38.95, MusicVolume: 0.4, MusicDropVolume: 0.841);
        var args = FfmpegArgumentBuilder.BuildNarrated(spec);
        // Quiet under the talking whatever the song; the measured gain only from the freeze on.
        Assert.Contains("volume=volume='if(lt(t\\,14)\\,0.16\\,0.841)':eval=frame[music]", args[args.ToList().IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void TheBellAndTheShutterAreLaidOverTheMix()
    {
        // The shutter starts a frame before the hand-over, so it is sounding as the white flash appears.
        Assert.Equal(2.97, NarratedStories.ShutterAt(3));
        Assert.Equal(0, NarratedStories.ShutterAt(0.01));
        // Each sound is the library's own file: the first name with the first word that matches anything.
        string[] library = [@"D:\sfx\Camera Whoosh.mp3", @"D:\sfx\Bell Ding.wav", @"D:\sfx\Camera Shutter.mp3", @"D:\sfx\Taco Bell Bong.mp3"];
        Assert.Equal(@"D:\sfx\Bell Ding.wav", NarratedStories.PickSound(library, "bell", "ding"));
        Assert.Equal(@"D:\sfx\Camera Shutter.mp3", NarratedStories.PickSound(library, "shutter", "camera"));
        Assert.Equal(@"D:\sfx\Camera Whoosh.mp3", NarratedStories.PickSound(library[..2], "shutter", "camera"));
        Assert.Null(NarratedStories.PickSound(library, "gong"));
        Assert.Null(NarratedStories.PickSound([], "bell"));

        FfmpegNarratedShot[] shots = [new("m1.mp4", 0, 3, 0.5, false), new("span.mp4", 3, 11, 0.5, true)];
        var title = new FfmpegNarratedImage("title.png", 1080, 70);
        FfmpegNarratedSound[] sounds = [new("ding.wav", 0), new("shutter.wav", NarratedStories.ShutterAt(3))];
        var spec = new FfmpegNarratedSpec(shots, "narration.mp3", 3, "out.mp4", 30, 20, title, MusicPath: "song.mp3", Sounds: sounds);
        var args = FfmpegArgumentBuilder.BuildNarrated(spec).ToList();
        var graph = args[args.IndexOf("-filter_complex") + 1];

        // Inputs: two shots, narration, song, title, then the sounds.
        Assert.Equal("ding.wav", args[args.IndexOf("shutter.wav") - 2]);
        Assert.Contains("[5:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=0:all=1,volume=1[sfx0]", graph);
        Assert.Contains("[6:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=2970:all=1,volume=1[sfx1]", graph);
        // Mixed as they are: no renormalising, so adding them leaves the voices and the song where they were.
        Assert.Contains("[voice][sfx0][sfx1][music]amix=inputs=4:duration=first:dropout_transition=2:normalize=0,alimiter=limit=0.9[aout]", graph);

        // Without music the sounds still play; without sounds the graph is what it always was.
        var silent = FfmpegArgumentBuilder.BuildNarrated(spec with { MusicPath = null }).ToList();
        Assert.Contains("[voice][sfx0][sfx1]amix=inputs=3:duration=first:dropout_transition=2:normalize=0,alimiter=limit=0.9[aout]",
            silent[silent.IndexOf("-filter_complex") + 1]);
        var plain = FfmpegArgumentBuilder.BuildNarrated(spec with { Sounds = null }).ToList();
        Assert.Contains("[voice][music]amix=inputs=2:duration=first:dropout_transition=2:normalize=0,alimiter=limit=0.9[aout]",
            plain[plain.IndexOf("-filter_complex") + 1]);
        Assert.DoesNotContain("sfx", plain[plain.IndexOf("-filter_complex") + 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgumentBuilder.BuildNarrated(spec with { Sounds = [new("", 0)] }));
    }

    [Fact]
    public void NudgingTheWindowMovesItEvenWhenItIsPinnedToAnEdge()
    {
        // A subject at 0.2 has its window pinned to the left edge (crop 0). Moving 0.04 of the frame right must move
        // the window 0.04 x 1920 = 77 px, not be swallowed by the pin.
        Assert.Equal(0, FfmpegArgumentBuilder.NarratedCropX(0.2));
        Assert.Equal(76, FfmpegArgumentBuilder.NarratedCropX(NarratedStories.ShiftFocus(0.2, 0.04)));
        // In the middle of the frame it is a plain move, either way.
        Assert.Equal(0.54, NarratedStories.ShiftFocus(0.5, 0.04), 3);
        Assert.Equal(0.46, NarratedStories.ShiftFocus(0.5, -0.04), 3);
        // And it stops at the frame's edge.
        Assert.Equal(1920 - FfmpegArgumentBuilder.NarratedVisibleWidth, FfmpegArgumentBuilder.NarratedCropX(NarratedStories.ShiftFocus(0.9, 0.5)));
    }

    [Fact]
    public void SwitchingTheSkullOffKeepsWhereItGoes()
    {
        // A clip made before the switch existed reads as "skull on".
        const string stored = """
            {"spanRelativePath":"span.mp4","spanWidth":1920,"spanHeight":1080,"pieces":[],"shots":[],"narration":"n",
             "narrationRelativePath":"narration.mp3","narrationSeconds":3,"words":[],"overlayTitle":"t","title":"t",
             "arrow":null,"skull":{"x":0.5,"y":0.4,"size":0.1},"episodeOffsetSeconds":0,"voice":"v"}
            """;
        var plan = JsonDefaults.Parse<NarratedPlanDto?>(stored, null)!;
        Assert.False(plan.SkullOff);
        // Off is a switch, not a deletion: the face it covered is still recorded, so it can be put back.
        var off = JsonDefaults.Parse<NarratedPlanDto?>(JsonSerializer.Serialize(plan with { SkullOff = true }, JsonDefaults.Serializer), null)!;
        Assert.True(off.SkullOff);
        Assert.Equal(new NarratedMarkDto(0.5, 0.4, 0.1), off.Skull);
    }

    [Fact]
    public void AMontageThatDoesNotFillTheNarrationIsRefused()
    {
        var spec = new FfmpegNarratedSpec(
            [new FfmpegNarratedShot("m1.mp4", 0, 1.75, 0.5, false), new FfmpegNarratedShot("span.mp4", 3, 6, 0.5, true)],
            "narration.mp3", 4, "out.mp4", 30, 20, new FfmpegNarratedImage("title.png", 1080, 70));
        Assert.Throws<ArgumentException>(() => FfmpegArgumentBuilder.BuildNarrated(spec));
    }

    [Fact]
    public void ANumberWhisperSplitStaysOneCaption()
    {
        var ass = CaptionBuilder.BuildNarratedAss(Words((0, 0.4, "like"), (0.4, 0.8, "$500"), (0.8, 1.2, ",000"), (1.3, 1.6, "one"),
            (1.6, 1.9, "-one"), (2.0, 2.4, "bag")), 3);
        var lines = ass.Split('\n').Where(x => x.StartsWith("Dialogue:")).Select(x => x[(x.LastIndexOf('}') + 1)..]).ToList();
        Assert.Equal(["like", "$500,000", "one-one", "bag"], lines);
    }

    [Fact]
    public void AnAudioTagSteersTheVoiceButIsNeverACaption()
    {
        using var document = JsonDocument.Parse("""
            {"characters":["[","h","y","p","e","]"," ","W","o","w"],
             "character_start_times_seconds":[0,0,0,0,0,0,0,0.1,0.2,0.3],
             "character_end_times_seconds":[0,0,0,0,0,0,0.1,0.2,0.3,0.4]}
            """);
        Assert.Equal(["Wow"], ElevenLabsClient.WordsFromAlignment(document.RootElement).Select(x => x.Word));
    }

    [Theory]
    [InlineData("eleven_v4", false)]
    [InlineData("eleven_v4_turbo", false)]
    [InlineData("eleven_v3", false)]
    [InlineData("eleven_multilingual_v2", true)]
    [InlineData("eleven_flash_v2_5", true)]
    public void OnlyTheOlderModelsAreSentStyleAndSpeakerBoost(string model, bool hasStyle)
    {
        var settings = JsonSerializer.Serialize(ElevenLabsClient.VoiceSettings(model));
        Assert.Equal(hasStyle, settings.Contains("\"style\""));
        Assert.Contains("\"stability\"", settings);
    }
}
