using System.Text.Json;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// Car clips go by the picture: where a car is on screen, which moments play there, which shots open the clip and
/// where the window sits. The vlog these numbers come from showed its Rolls-Royce for about two minutes in 34.
/// </summary>
public sealed class CarScenesTests
{
    private static readonly IReadOnlyList<CarScene> Rolls =
    [
        new(60, 100, CarScenes.Exterior, "A red Rolls-Royce coming off a truck", true, false, [70, 90]),
        new(190, 250, CarScenes.Interior, "The open cabin in the garage", true, false, [200]),
        new(270, 350, CarScenes.Driving, "He drives, the camera on his face", true, false, []),
        new(350, 430, CarScenes.Exterior, "The car at a petrol pump", true, false, [350, 400]),
        new(960, 1010, CarScenes.Exterior, "The car parked at night", false, true, [970]),
    ];

    private static JsonElement Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public void ASceneRunsOneThumbnailPastItsLastLabel()
    {
        var scenes = CarScenes.Parse(Json(new
        {
            framesInspected = true,
            scenes = new object[]
            {
                new { fromSeconds = 350, toSeconds = 420, view = "exterior", shows = "at the pump", creatorVisible = true, dark = false, bestSeconds = new[] { 350, 400, 900 } },
                new { fromSeconds = 60, toSeconds = 60, view = "Exterior", shows = "one thumbnail", creatorVisible = false, dark = false, bestSeconds = Array.Empty<int>() },
                new { fromSeconds = 2040, toSeconds = 2040, view = "interior", shows = "the last thumbnail", creatorVisible = false, dark = true, bestSeconds = Array.Empty<int>() },
                new { fromSeconds = 500, toSeconds = 520, view = "a toy car", shows = "not a view", creatorVisible = false, dark = false, bestSeconds = Array.Empty<int>() },
                new { fromSeconds = 5000, toSeconds = 5010, view = "exterior", shows = "past the end", creatorVisible = false, dark = false, bestSeconds = Array.Empty<int>() },
            },
        }), durationSeconds: 2043, intervalSeconds: 10);

        Assert.Equal([60, 350, 2040], scenes.Select(x => x.FromSeconds));
        Assert.Equal([70, 430, 2043], scenes.Select(x => x.ToSeconds));
        // A "best" thumbnail outside its own scene is a slip, and would open the clip on something else.
        Assert.Equal([350, 400], scenes[1].BestSeconds);
        Assert.True(scenes[2].Dark);
    }

    [Fact]
    public void NoScenesIsAnAnswerNotAnError() =>
        Assert.Empty(CarScenes.Parse(Json(new { framesInspected = true, scenes = Array.Empty<object>() }), 600, 10));

    [Fact]
    public void AMomentCountsWhenACarIsOnScreenForHalfOfIt()
    {
        // In the scene, and just before it: a thumbnail stands for the seconds around it.
        Assert.True(CarScenes.IsCarMoment([(365, 380)], Rolls));
        Assert.True(CarScenes.IsCarMoment([(52, 66)], Rolls));
        // At home, twenty minutes from any car.
        Assert.False(CarScenes.IsCarMoment([(1500, 1518)], Rolls));
        // Mostly elsewhere, with one piece beside the car.
        Assert.False(CarScenes.IsCarMoment([(600, 615), (365, 370)], Rolls));
        Assert.Equal(0.25, CarScenes.ShareOnScreen([(600, 615), (365, 370)], Rolls), 2);
    }

    /// <summary>This is what the transcript picked as car clips: him at the wheel, the car barely seen.</summary>
    [Fact]
    public void HisFaceAtTheWheelIsNotTheCarOnScreen()
    {
        Assert.False(CarScenes.IsCarMoment([(295, 312)], Rolls));
        Assert.Null(CarScenes.At(Rolls, 300));
        Assert.Equal("The open cabin in the garage", CarScenes.At(Rolls, 252)?.Shows);
    }

    [Fact]
    public void ScenesThatRunTogetherAreNotCountedTwice() =>
        Assert.Equal(1, CarScenes.ShareOnScreen([(90, 200)],
            [new(60, 100, CarScenes.Exterior, "", true, false, []), new(105, 250, CarScenes.Interior, "", true, false, [])]), 3);

    [Fact]
    public void TheOpeningShotsComeFromWhereTheCarIs()
    {
        var picked = new List<MontagePick>
        {
            new(1300, "TJR at his desk, facing the camera"),   // kept: the arrow needs him
            new(1410, "the trading screen"),                   // no car there
            new(372, "the car at the pump"),                   // in a scene
            new(300, "him driving"),                           // his face at the wheel
        };
        var montage = CarScenes.Montage(picked, Rolls, (365, 380), 8);

        Assert.Equal(1300, montage[0].AtSeconds);
        Assert.DoesNotContain(montage, x => x.AtSeconds is 1410 or 300);
        // 372 is inside the moment itself, which must not be shown twice.
        Assert.DoesNotContain(montage, x => x.AtSeconds is >= 362 and <= 383);
        // Him at the wheel comes second, for the arrow; then the scenes: the outside in daylight, the longer scene
        // first, then the cabin, the night shot last.
        Assert.Equal([1300, 310, 350, 70, 400, 90, 200, 970], montage.Select(x => x.AtSeconds));
        Assert.Equal("He drives, the camera on his face", montage[1].Shows);
        Assert.Equal("A red Rolls-Royce coming off a truck", montage[3].Shows);
        // Asked for more, the scenes give more than their best thumbnails: a moment every ten seconds through
        // each, still outside the moment and apart from the rest.
        Assert.Equal([1300, 310, 350, 70, 400, 90, 200, 970, 360, 60, 80, 390],
            CarScenes.Montage(picked, Rolls, (365, 380), 12).Select(x => x.AtSeconds));
    }

    /// <summary>Asked shot by shot whether a bonnet across the bottom of the picture was a car shot, the review said
    /// yes one time in two. Asked to put the car shots in order, it put that one last, and the last ones are not shown.</summary>
    [Fact]
    public void OnlyTheBestFewCarShotsAreShown()
    {
        // Seven car shots in the order they were found, with the review's ranks: the best five stay.
        Assert.Equal([0, 1, 3, 4, 6], CarScenes.Best([2, 5, 7, 1, 3, 6, 4]).Order());
        // A shot it gave no rank comes after those it ranked; fewer shots than places all stay.
        Assert.Equal([0, 2], CarScenes.Best([1, null, 2], 2).Order());
        Assert.Equal([0, 1], CarScenes.Best([null, 3]).Order());
        // Two shots given the same rank: the one found first.
        Assert.Equal([0], CarScenes.Best([1, 1], 1));
        Assert.Empty(CarScenes.Best([]));
    }

    /// <summary>The Hellcat giveaway vlog. Its first car on screen is a red one parked in a lot, and a clip about
    /// the Hellcat was offered that before the Hellcat itself.</summary>
    [Fact]
    public void ScenesThatShowWhatTheStoryIsAboutComeFirst()
    {
        var scenes = new List<CarScene>
        {
            new(90, 100, CarScenes.Exterior, "A small red car sits parked in a lot", false, false, [90]),
            new(940, 960, CarScenes.Exterior, "Rows of cars sit in the dealership's outdoor lot", false, false, [940, 950]),
            new(1400, 1520, CarScenes.Exterior, "Key handover: he hugs the recipient beside the purple Hellcat", true, false, [1400, 1410, 1480, 1490]),
            new(1600, 1610, CarScenes.Exterior, "Close-up of the purple Hellcat's glossy body", false, false, [1600]),
            new(2760, 3070, CarScenes.Exterior, "The purple Challenger parked at a graffiti-covered site", true, false, [2810, 2900, 3010]),
        };
        const string story = "Trader Gives Stranger A Free Hellcat. This trader told a Wendy's worker the Hellcat was his.";
        Assert.Equal(1, CarScenes.Relevance(story, scenes[2].Shows));
        Assert.Equal(1, CarScenes.Relevance(story, scenes[3].Shows));
        Assert.Equal(0, CarScenes.Relevance(story, scenes[0].Shows));
        Assert.Equal(0, CarScenes.Relevance(null, scenes[2].Shows));
        // Words every car vlog shares ("creator", "driving", "trader") tell nothing apart.
        Assert.Equal(0, CarScenes.Relevance("This trader is driving", "The creator is driving, talking to the camera"));

        // The Hellcat's own scenes first, all they have: their best thumbnails, then moments through the handover.
        // The other cars and places come only after.
        var montage = CarScenes.Montage([], scenes, (1342, 1350), 6, story);
        Assert.Equal([1400, 1600, 1410, 1480, 1490, 1420], montage.Select(x => x.AtSeconds));
        // One scene gives five at most, so a clip does not repeat itself; the rest come from the other scenes.
        Assert.Equal([2810, 940, 90, 2900, 950, 3010, 2760, 2770],
            CarScenes.Montage([], scenes, (1342, 1350), 14, story).Skip(6).Select(x => x.AtSeconds));
        // Without a story the order is the scenes' own.
        Assert.Equal([2810, 1400, 940, 90, 1600, 2900], CarScenes.Montage([], scenes, (1342, 1350), 6).Select(x => x.AtSeconds));
    }

    /// <summary>The first car clips opened with the arrow on a stranger sitting in his car. A shot of the creator at
    /// the wheel is the one kind sure to be him, so one or two are always on offer.</summary>
    [Fact]
    public void TheCreatorAtTheWheelIsOfferedForTheArrow()
    {
        var scenes = new List<CarScene>
        {
            new(100, 120, CarScenes.Driving, "someone tries the seat", true, false, [110]),
            new(160, 440, CarScenes.Driving, "the long drive, talking to the camera", true, false, [200, 300]),
            new(500, 540, CarScenes.Driving, "a fan at the wheel", false, false, [510]),
            new(600, 680, CarScenes.Driving, "another drive", true, false, []),
            new(710, 720, CarScenes.Exterior, "the car at a meet", false, false, [710]),
        };
        // Long stretches only, longest first, and never the stretch where someone else drives.
        Assert.Equal([200, 640, 300], CarScenes.AtTheWheel(scenes).Select(x => x.AtSeconds));
        Assert.All(CarScenes.AtTheWheel(scenes), x => Assert.True(x.CreatorAtWheel));

        var one = CarScenes.Montage([], scenes, (810, 832));
        Assert.Equal([200, 640, 710], one.Select(x => x.AtSeconds));
        Assert.Equal([true, true, false], one.Select(x => x.CreatorAtWheel));
        // Another moment of the same video starts elsewhere in the list, so the two clips open differently.
        Assert.Equal([300, 200, 710], CarScenes.Montage([], scenes, (905, 920)).Select(x => x.AtSeconds));
    }

    [Fact]
    public void AShortStretchAtTheWheelIsUsedOnlyWhenThereIsNoLongOne() =>
        Assert.Equal([110], CarScenes.AtTheWheel([new(100, 120, CarScenes.Driving, "a short drive", true, false, [110])])
            .Select(x => x.AtSeconds));

    [Theory]
    [InlineData("Short bleached-blond hair, slim, a dark grey t-shirt and a pink cord necklace.", "Short bleached-blond hair, slim, a dark grey t-shirt and a pink cord necklace.")]
    [InlineData("  Curly dark hair.\n He wears a grey t-shirt, later a white one.  ", "Curly dark hair. He wears a grey t-shirt, later a white one.")]
    [InlineData("   ", null)]
    public void WhatTheCreatorLooksLikeIsKeptAsOneLine(string given, string? expected) =>
        Assert.Equal(expected, CarScenes.ParseCreator(Json(new { creator = given, scenes = Array.Empty<object>() })));

    [Fact]
    public void AReplyThatDoesNotDescribeTheCreatorSaysNothing() =>
        Assert.Null(CarScenes.ParseCreator(Json(new { scenes = Array.Empty<object>() })));

    [Fact]
    public void OpeningShotsKeepApartFromEachOther()
    {
        var scenes = new List<CarScene> { new(100, 140, CarScenes.Exterior, "the car", true, false, [100, 104, 110, 130]) };
        Assert.Equal([100, 110, 130, 120], CarScenes.Montage([], scenes, (900, 915)).Select(x => x.AtSeconds));
    }

    [Fact]
    public void ASceneWithNoBestThumbnailOffersItsMiddleFirst() =>
        Assert.Equal([120, 100, 110, 130], CarScenes.Montage([], [new(100, 140, CarScenes.Exterior, "the car", false, false, [])], (900, 915))
            .Select(x => x.AtSeconds));

    [Fact]
    public void TheWindowCentresOnTheCarWhenNobodyIsInTheShot()
    {
        Assert.Equal(0.8, CarScenes.Frame(null, 0.8));
        Assert.Equal(0.5, CarScenes.Frame(null, null));
        Assert.Equal(0.3, CarScenes.Frame(0.3, null));
    }

    /// <summary>The arrow and the skull are drawn on his face, so the window may not leave it behind.</summary>
    [Fact]
    public void TheWindowMovesTowardTheCarButKeepsHisFaceInside()
    {
        var visible = FfmpegArgumentBuilder.NarratedVisibleWidth / 1920d;
        var focus = CarScenes.Frame(0.5, 0.9);
        Assert.InRange(focus, 0.6, 0.7);
        var leftEdge = FfmpegArgumentBuilder.NarratedCropX(focus) / 1920d;
        Assert.InRange(0.5 - leftEdge, CarScenes.FaceMargin - 0.002, CarScenes.FaceMargin + 0.002);
        Assert.True(0.9 < leftEdge + visible);

        // A car right beside him needs no clamping at all.
        Assert.Equal(0.55, CarScenes.Frame(0.5, 0.55));
        // The opening shot gives the arrow more room.
        Assert.True(CarScenes.Frame(0.5, 0.9, CarScenes.OpenerFaceMargin) < focus);

        // The same on the other side: a car to his left leaves his face that far inside the window's right edge.
        var rightEdge = FfmpegArgumentBuilder.NarratedCropX(CarScenes.Frame(0.5, 0.1)) / 1920d + visible;
        Assert.InRange(rightEdge - 0.5, CarScenes.FaceMargin - 0.002, CarScenes.FaceMargin + 0.002);
    }

    [Theory]
    [InlineData("cars", "cars")]
    [InlineData(" Cars ", "cars")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("boats", null)]
    public void OnlyTheCarsFocusIsKnown(string? given, string? expected)
    {
        Assert.Equal(expected, PipelineFocus.Normalise(given));
        Assert.Equal(given != "boats", PipelineFocus.IsKnown(given));
    }

    [Fact]
    public void ACarClipsRunDropsMomentsWithoutTheCarAndTakesShortOnes()
    {
        var transcript = Enumerable.Range(0, 4000).Select(i => new TranscriptWord(i * 0.5, i * 0.5 + 0.4, $"word{i}")).ToList();
        var narration = "This 24 year old trader had a vintage Rolls-Royce delivered to his door in Puerto Rico, and what happened at the pump is crazy.";
        object Story(string title, double start, double end) => new
        {
            title, caption = "TJR and the Rolls", overlayTitle = "POV: The pump wins 💀", narration, teller = "TJR",
            emphasis = Array.Empty<string>(), score = 0.9, rationale = "",
            pieces = new[] { new { startSeconds = start, endSeconds = end, firstWords = "", lastWords = "" } },
            montage = new[] { new { atSeconds = 1300.0, shows = "TJR at his desk" }, new { atSeconds = 1410.0, shows = "the trading screen" },
                new { atSeconds = 1500.0, shows = "his kitchen" } },
        };
        var root = Json(new
        {
            stories = new[] { Story("At the pump", 365, 374), Story("At his desk", 1500, 1516), Story("At the wheel", 295, 311) },
        });

        // An ordinary run keeps the two long enough, wherever they play.
        Assert.Equal(["At his desk", "At the wheel"],
            NarratedStories.Parse(root, 2043, transcript).Select(x => x.Story.Title).Order());

        var cars = NarratedStories.Parse(root, 2043, transcript, CarScenes.MinimumMomentSeconds, Rolls);
        var story = Assert.Single(cars);
        Assert.Equal("At the pump", story.Story.Title);
        Assert.Equal(1300, story.Montage[0].AtSeconds);
        // After the first pick: him at the wheel once, then only where the car is on screen.
        Assert.Equal(310, story.Montage[1].AtSeconds);
        Assert.All(story.Montage.Skip(2), x => Assert.NotNull(CarScenes.At(Rolls, x.AtSeconds)));
        Assert.True(story.Montage.Count >= 5);
    }

    /// <summary>The Rolls vlog as a look for everything would report it: the car, a villa, a watch, and his face.</summary>
    private static readonly CarSceneScan Everything = new(10, 204,
    [
        .. Rolls,
        new(500, 560, CarScenes.Shown, "A villa with a pool in Miami", false, false, [510, 540], "home"),
        new(700, 710, CarScenes.Shown, "A gold Rolex held to the camera", true, false, [700], "watch"),
    ], DateTimeOffset.UnixEpoch, "Short dark hair, black hoodie", CarSceneScan.Everything, [1200, 1500]);

    [Fact]
    public void ALookForEverythingSaysWhatKindOfThingAStretchShows()
    {
        object Scene(double from, string? kind, string view) => kind is null
            ? new { fromSeconds = from, toSeconds = from, view, shows = "", creatorVisible = false, dark = false, bestSeconds = Array.Empty<int>() }
            : new { fromSeconds = from, toSeconds = from, kind, view, shows = "", creatorVisible = false, dark = false, bestSeconds = Array.Empty<int>() };
        var scenes = CarScenes.Parse(Json(new
        {
            scenes = new[]
            {
                Scene(10, "home", "exterior"), Scene(20, "Watch", "shown"), Scene(30, "car", "driving"), Scene(40, "car", "shown"),
                Scene(50, "a nice street", "shown"), Scene(60, null, "interior"),
            },
        }), durationSeconds: 600, intervalSeconds: 10);

        Assert.Equal([10, 20, 30, 60], scenes.Select(x => x.FromSeconds));
        // Anything but a car is simply shown, whatever view the reply gave it; a car is never just "shown".
        Assert.Equal([CarScenes.Shown, CarScenes.Shown, CarScenes.Driving, CarScenes.Interior], scenes.Select(x => x.View));
        Assert.Equal([false, false, true, true], scenes.Select(x => x.IsCar));
        Assert.Equal([true, true, false, true], scenes.Select(x => x.OnScreen));
    }

    [Fact]
    public void ACarClipGoesOnlyByTheCarsOfALookForEverything()
    {
        Assert.Equal(Rolls, Everything.Cars);
        // A look saved before it looked for anything else has only cars, and no kind written down.
        Assert.True((Rolls[0] with { Kind = null! }).IsCar);
        // His face, in time order, each thumbnail once and none past the end of the video.
        Assert.Equal([20, 1200, 1500], CarScenes.ParseCreatorSeconds(Json(new { creatorSeconds = new[] { 1500, 1200, 20, 9000, 1200 } }), 2043));
    }

    [Fact]
    public void AnOrdinaryClipIsOfferedWhatTheVideoHasToShow()
    {
        IReadOnlyList<MontagePick> picked = [new(1300, "TJR at his desk"), new(1410, "the trading screen"), new(1500, "his kitchen")];

        var picks = CarScenes.Showcase(picked, Everything, (1600, 1620), 12,
            "This trader bought a villa in Miami and what he found by the pool is crazy");

        // His first pick, two moments sure to be him, all the villa has, the rest of his picks, then the best of
        // the other scenes: daylight before night, and the pump, the longest, before the truck and the watch.
        Assert.Equal([1300, 1200, 1500, 510, 540, 500, 520, 530, 1410, 350, 70, 700], picks.Select(x => x.AtSeconds));
        Assert.Equal([false, true, true], picks.Take(3).Select(x => x.CreatorAtWheel));
        // A video with a long stretch at the wheel offers that before the thumbnails of his face.
        Assert.Equal(310, CarScenes.SurelyTheCreator(Everything)[0].AtSeconds);
    }

    [Theory]
    [InlineData("we just bought a hellcat for a stranger", "This car collector bought a purple Hellcat", "collector")]
    [InlineData("we just bought a hellcat for a stranger", "This trader bought a purple Hellcat", null)]
    [InlineData("this is the newest one in my collection", "This car collector bought a purple Hellcat", null)]
    [InlineData("we just bought a hellcat My Car Collection Tour", "This Hellcat collector kept it", null)]
    public void AStoryMayNotCallHimWhatTheVideoNeverDoes(string said, string narration, string? expected) =>
        Assert.Equal(expected, NarratedStories.UnsaidLabel(said, narration, "POV: He kept it 💀", null));

    [Fact]
    public void ASceneSuitsAStoryThatSpeaksOfItsKind()
    {
        var cabin = new CarScene(210, 230, CarScenes.Shown, "A cabin with cream leather seats", true, false, [210], "jet");
        Assert.Equal(1, CarScenes.Relevance("This trader chartered a private jet to Coachella", cabin));
        Assert.Equal(0, CarScenes.Relevance("This trader lost $41,000 before breakfast", cabin));
        // A car goes by its description alone, as it always has.
        Assert.Equal(0, CarScenes.Relevance("This trader chartered a private jet to Coachella", Rolls[0]));
        Assert.Equal(2, CarScenes.Relevance("His Rolls-Royce arrives", Rolls[0]));
    }

    [Fact]
    public void AnOrdinaryRunKeepsEveryMomentAndAddsWhatThereIsToShow()
    {
        var transcript = Enumerable.Range(0, 4000).Select(i => new TranscriptWord(i * 0.5, i * 0.5 + 0.4, $"word{i}")).ToList();
        var root = Json(new
        {
            stories = new[]
            {
                new
                {
                    title = "At his desk", caption = "TJR and the Rolls", overlayTitle = "POV: The desk wins 💀", teller = "TJR",
                    narration = "This 24 year old trader had a vintage Rolls-Royce delivered to his door in Puerto Rico, and what happened at his desk is crazy.",
                    emphasis = Array.Empty<string>(), score = 0.9, rationale = "",
                    pieces = new[] { new { startSeconds = 1500.0, endSeconds = 1516.0, firstWords = "", lastWords = "" } },
                    montage = new[] { new { atSeconds = 1300.0, shows = "TJR at his desk" }, new { atSeconds = 1410.0, shows = "the trading screen" },
                        new { atSeconds = 1700.0, shows = "his kitchen" } },
                },
            },
        });

        // No car is on screen while he talks at his desk; the moment stays, and the car is shown under the narrator.
        var story = Assert.Single(NarratedStories.Parse(root, 2043, transcript, shown: Everything));
        Assert.Equal(1300, story.Montage[0].AtSeconds);
        Assert.Contains(story.Montage, x => x.AtSeconds == 1410);
        Assert.Contains(story.Montage, x => x.Shows.Contains("Rolls-Royce"));
        Assert.True(story.Montage.Count > 3);
    }
}
