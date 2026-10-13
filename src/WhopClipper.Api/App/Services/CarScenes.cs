using System.Text.Json;

namespace WhopClipper.Api.App.Services;

/// <summary>What a run is steered toward. Null is an ordinary run.</summary>
public static class PipelineFocus
{
    /// <summary>Car clips: the moment and its opening shots come from where a car is on screen.</summary>
    public const string Cars = "cars";

    public static bool IsKnown(string? value) => string.IsNullOrWhiteSpace(value) || Normalise(value) is not null;

    public static string? Normalise(string? value) =>
        string.Equals(value?.Trim(), Cars, StringComparison.OrdinalIgnoreCase) ? Cars : null;
}

/// <summary>A stretch of a video where a car, or another sign of the creator's wealth, is in the picture, found by
/// looking at its thumbnails.</summary>
/// <param name="View">For a car, "exterior": its outside is in shot. "interior": the cabin itself is being shown.
/// "driving": someone sits in it talking to the camera, so the picture is their face. Anything else is "shown".</param>
/// <param name="BestSeconds">Thumbnails in the stretch that show it best, for the opening shots.</param>
/// <param name="Kind">What is shown: a car unless the look was for everything (<see cref="CarSceneScan.Everything"/>),
/// which also finds homes, jets, yachts, watches and cash.</param>
public sealed record CarScene(double FromSeconds, double ToSeconds, string View, string Shows, bool CreatorVisible,
    bool Dark, IReadOnlyList<double> BestSeconds, string Kind = CarScenes.Car)
{
    /// <summary>The thing itself can be seen. A face at the wheel does not count: that is what a transcript-picked
    /// "car clip" already was, and the car was barely visible in it.</summary>
    public bool OnScreen => View is CarScenes.Exterior or CarScenes.Interior or CarScenes.Shown;

    /// <summary>A scan saved before it looked for anything but cars has no kind.</summary>
    public bool IsCar => string.IsNullOrEmpty(Kind) || Kind == CarScenes.Car;
}

/// <summary>The result of looking at one video, kept beside its transcript so it is looked at once.</summary>
/// <param name="Creator">What the video's creator looks like, so a later look at single shots can tell him from a
/// friend or a fan. The opening arrow points at "this 24 year old", and in the first car clips made it pointed at
/// the stranger sitting in his car.</param>
/// <param name="Version">1 looked for cars only. <see cref="Everything"/> looked for every sign of wealth.</param>
/// <param name="CreatorSeconds">Thumbnails where the creator faces the camera alone, so a later look at single shots
/// has his face to go by in a video where he never sits at the wheel.</param>
public sealed record CarSceneScan(double IntervalSeconds, int Frames, IReadOnlyList<CarScene> Scenes, DateTimeOffset ScannedAt,
    string? Creator = null, int Version = 1, IReadOnlyList<double>? CreatorSeconds = null)
{
    public const int Everything = 2;

    /// <summary>The scenes a car clip goes by.</summary>
    public IReadOnlyList<CarScene> Cars => Scenes.Where(x => x.IsCar).ToList();
}

public static class CarScenes
{
    public const string Exterior = "exterior";
    public const string Interior = "interior";
    public const string Driving = "driving";

    /// <summary>The view of everything that is not a car: it is in the picture as the subject.</summary>
    public const string Shown = "shown";

    public const string Car = "car";

    /// <summary>What a look for everything may call a scene, besides a car.</summary>
    private static readonly string[] OtherKinds = ["home", "jet", "yacht", "watch", "cash", "other"];

    /// <summary>The look for everything worth showing: a scene also says what kind of thing it is, and the reply
    /// names thumbnails of the creator facing the camera.</summary>
    public const string EverythingSchema = """
    {"title":"WhopClipperScenesV2","type":"object","additionalProperties":false,"required":["framesInspected","creator","creatorSeconds","scenes"],"properties":{"framesInspected":{"type":"boolean"},"creator":{"type":"string"},"creatorSeconds":{"type":"array","maxItems":6,"items":{"type":"number","minimum":0}},"scenes":{"type":"array","maxItems":60,"items":{"type":"object","additionalProperties":false,"required":["fromSeconds","toSeconds","kind","view","shows","creatorVisible","dark","bestSeconds"],"properties":{"fromSeconds":{"type":"number","minimum":0},"toSeconds":{"type":"number","minimum":0},"kind":{"type":"string","enum":["car","home","jet","yacht","watch","cash","other"]},"view":{"type":"string","enum":["exterior","interior","driving","shown"]},"shows":{"type":"string"},"creatorVisible":{"type":"boolean"},"dark":{"type":"boolean"},"bestSeconds":{"type":"array","maxItems":4,"items":{"type":"number","minimum":0}}}}}}}
    """;

    /// <summary>The montage review of an ordinary clip from a video whose picture has been looked at: where the thing
    /// worth showing sits in each shot. Rows labelled P are the moment itself.</summary>
    public const string ShowReviewSchema = """
    {"title":"WhopClipperShowMontageReviewV1","type":"object","additionalProperties":false,"required":["framesInspected","shots","slots"],"properties":{"framesInspected":{"type":"boolean"},"shots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["label","approved","weak","weakFrames","showsPerson","showsCreator","showsSubject","subjectView","subjectRank","subjectX","visibleContent","reason"],"properties":{"label":{"type":"string"},"approved":{"type":"boolean"},"weak":{"type":"boolean"},"weakFrames":{"type":"array","maxItems":3,"items":{"type":"integer","minimum":1,"maximum":3}},"subjectRank":{"type":["integer","null"],"minimum":1},"subjectView":{"type":"string","enum":["whole","part","edge","none"]},"showsPerson":{"type":"boolean"},"showsCreator":{"type":"boolean"},"showsSubject":{"type":"boolean"},"subjectX":{"type":["number","null"],"minimum":0,"maximum":1},"visibleContent":{"type":"string"},"reason":{"type":"string"}}}},"slots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["slot","label"],"properties":{"slot":{"type":"string"},"label":{"type":["string","null"]}}}}}}
    """;

    public const string Schema = """
    {"title":"WhopClipperCarScenesV1","type":"object","additionalProperties":false,"required":["framesInspected","creator","scenes"],"properties":{"framesInspected":{"type":"boolean"},"creator":{"type":"string"},"scenes":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["fromSeconds","toSeconds","view","shows","creatorVisible","dark","bestSeconds"],"properties":{"fromSeconds":{"type":"number","minimum":0},"toSeconds":{"type":"number","minimum":0},"view":{"type":"string","enum":["exterior","interior","driving"]},"shows":{"type":"string"},"creatorVisible":{"type":"boolean"},"dark":{"type":"boolean"},"bestSeconds":{"type":"array","maxItems":4,"items":{"type":"number","minimum":0}}}}}}}
    """;

    /// <summary>The montage review, with where the car sits in each shot. Rows labelled P are the moment itself.</summary>
    public const string ReviewSchema = """
    {"title":"WhopClipperCarMontageReviewV1","type":"object","additionalProperties":false,"required":["framesInspected","shots","slots"],"properties":{"framesInspected":{"type":"boolean"},"shots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["label","approved","weak","weakFrames","showsPerson","showsCreator","showsCar","carView","bonnetOnly","carRank","carX","visibleContent","reason"],"properties":{"label":{"type":"string"},"approved":{"type":"boolean"},"weak":{"type":"boolean"},"bonnetOnly":{"type":"boolean"},"weakFrames":{"type":"array","maxItems":3,"items":{"type":"integer","minimum":1,"maximum":3}},"carRank":{"type":["integer","null"],"minimum":1},"carView":{"type":"string","enum":["whole","part","edge","none"]},"showsPerson":{"type":"boolean"},"showsCreator":{"type":"boolean"},"showsCar":{"type":"boolean"},"carX":{"type":["number","null"],"minimum":0,"maximum":1},"visibleContent":{"type":"string"},"reason":{"type":"string"}}}},"slots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["slot","label"],"properties":{"slot":{"type":"string"},"label":{"type":["string","null"]}}}}}}
    """;

    /// <summary>What the review says of a shot whose car is only a hood, a bumper or a corner at the frame's edge.
    /// Such a shot is weak in a car clip: it is the "I can barely see the car" shot.</summary>
    public const string EdgeView = "edge";

    /// <summary>How many car shots a clip shows: the best few by the review's own ranking. Asked whether one shot is
    /// good enough, the review wavers on the borderline ones (a bonnet across the bottom of the picture passed one
    /// time in two); asked to put them in order, it puts those last every time.</summary>
    public const int CarShotsKept = 5;

    /// <summary>Which car shots are shown, as positions in <paramref name="ranks"/>: the best
    /// <paramref name="keep"/>, rank 1 being the best. A shot the review gave no rank comes after those it ranked.</summary>
    public static IReadOnlySet<int> Best(IReadOnlyList<int?> ranks, int keep = CarShotsKept) =>
        ranks.Select((rank, index) => (rank, index)).OrderBy(x => x.rank ?? int.MaxValue).ThenBy(x => x.index)
            .Take(Math.Max(0, keep)).Select(x => x.index).ToHashSet();

    /// <summary>A thumbnail stands for the seconds around it, and a section download never starts on the second
    /// asked for, so a moment may sit this far outside a scene and still count as in it.</summary>
    public const double ToleranceSeconds = 10;

    /// <summary>A car clip may be one short line: the lines another channel cut from these vlogs ran ten seconds.</summary>
    public const double MinimumMomentSeconds = 7;

    /// <summary>How much of the frame's width the window shows.</summary>
    private const double Visible = FfmpegArgumentBuilder.NarratedVisibleWidth / 1920d;

    /// <summary>How far inside the window's edge a face must stay: far enough for the skull that covers it.</summary>
    public const double FaceMargin = 0.12;

    /// <summary>The opening shot's face keeps more room, for the arrow that points at it from the left.</summary>
    public const double OpenerFaceMargin = 0.16;

    /// <summary>
    /// Reads the scenes the AI reported. It gives the labels of a scene's first and last thumbnail; the last one
    /// stands for the seconds after it too, so the scene runs one interval past it.
    /// </summary>
    public static IReadOnlyList<CarScene> Parse(JsonElement root, double durationSeconds, double intervalSeconds)
    {
        var result = new List<CarScene>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in scenes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Number(item, "fromSeconds", out var from) ||
                !Number(item, "toSeconds", out var last)) continue;
            var view = item.TryGetProperty("view", out var v) ? (v.GetString() ?? "").Trim().ToLowerInvariant() : "";
            // Only a look for everything says what kind of thing a scene shows; without it, it is a car.
            var kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? (k.GetString() ?? "").Trim().ToLowerInvariant() : Car;
            if (OtherKinds.Contains(kind)) view = Shown;
            else if (kind != Car) continue;
            if (view is not (Exterior or Interior or Driving or Shown) || kind == Car && view == Shown) continue;
            var to = Math.Min(durationSeconds, Math.Max(from, last) + intervalSeconds);
            if (from < 0 || from >= durationSeconds || to <= from) continue;
            var best = new List<double>();
            if (item.TryGetProperty("bestSeconds", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var entry in list.EnumerateArray())
                    if (entry.TryGetDouble(out var at) && at >= from && at < to) best.Add(at);
            result.Add(new CarScene(from, to, view, item.TryGetProperty("shows", out var shows) ? shows.GetString() ?? "" : "",
                Flag(item, "creatorVisible"), Flag(item, "dark"), best, kind));
        }
        return result.OrderBy(x => x.FromSeconds).ToList();
    }

    /// <summary>The thumbnails a look for everything named as the creator facing the camera, in time order.</summary>
    public static IReadOnlyList<double> ParseCreatorSeconds(JsonElement root, double durationSeconds) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("creatorSeconds", out var list) &&
        list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble())
                .Where(x => x >= 0 && x < durationSeconds).Distinct().Order().ToList()
            : [];

    /// <summary>How much of a moment plays while a car is on screen, 0 to 1.</summary>
    public static double ShareOnScreen(IReadOnlyList<(double Start, double End)> pieces, IReadOnlyList<CarScene> scenes,
        double toleranceSeconds = ToleranceSeconds)
    {
        var total = pieces.Sum(x => Math.Max(0, x.End - x.Start));
        if (total <= 0) return 0;
        // Widened scenes can run into each other; joined first, so no second is counted twice.
        var spans = new List<(double Start, double End)>();
        foreach (var scene in scenes.Where(x => x.OnScreen).OrderBy(x => x.FromSeconds))
        {
            var (start, end) = (scene.FromSeconds - toleranceSeconds, scene.ToSeconds + toleranceSeconds);
            if (spans.Count > 0 && start <= spans[^1].End) spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
            else spans.Add((start, end));
        }
        var inside = pieces.Sum(piece => spans.Sum(span => Math.Max(0, Math.Min(piece.End, span.End) - Math.Max(piece.Start, span.Start))));
        return Math.Clamp(inside / total, 0, 1);
    }

    /// <summary>A car clip is a moment at least half of which plays with a car on screen.</summary>
    public static bool IsCarMoment(IReadOnlyList<(double Start, double End)> pieces, IReadOnlyList<CarScene> scenes) =>
        ShareOnScreen(pieces, scenes) >= 0.5;

    /// <summary>The on-screen scene a second of the video falls in, give or take a little.</summary>
    public static CarScene? At(IReadOnlyList<CarScene> scenes, double seconds, double toleranceSeconds = 5) =>
        scenes.Where(x => x.OnScreen && seconds >= x.FromSeconds - toleranceSeconds && seconds <= x.ToSeconds + toleranceSeconds)
            .OrderBy(x => seconds >= x.FromSeconds && seconds <= x.ToSeconds ? 0 : 1).FirstOrDefault();

    /// <summary>Stored when a look at the video did not say what the creator looks like. No arrow is drawn then:
    /// one pointing at the wrong person is worse than none.</summary>
    public const string CreatorUnknown = "unknown";

    /// <summary>What a reply says the creator looks like, as one line; null when it says nothing.</summary>
    public static string? ParseCreator(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("creator", out var creator) &&
        creator.ValueKind == JsonValueKind.String &&
        string.Join(' ', (creator.GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } text
            ? text[..Math.Min(text.Length, 400)]
            : null;

    /// <summary>A stretch at the wheel this long is the creator talking to his camera; a shorter one may be a friend
    /// or a fan trying the seat.</summary>
    public const double LongDriveSeconds = 30;

    /// <summary>
    /// Moments of the creator at the wheel talking to the camera, the surest first: the best thumbnail of each long
    /// stretch, longest stretch first, then their second bests. Shorter stretches are used only when there is no
    /// long one.
    /// </summary>
    public static IReadOnlyList<MontagePick> AtTheWheel(IReadOnlyList<CarScene> scenes)
    {
        var drives = scenes.Where(x => x.View == Driving && x.CreatorVisible)
            .OrderByDescending(x => x.ToSeconds - x.FromSeconds).ToList();
        if (drives.Any(x => x.ToSeconds - x.FromSeconds >= LongDriveSeconds))
            drives = drives.Where(x => x.ToSeconds - x.FromSeconds >= LongDriveSeconds).ToList();
        return drives.SelectMany(scene =>
                (scene.BestSeconds.Count > 0 ? scene.BestSeconds : [Math.Floor((scene.FromSeconds + scene.ToSeconds) / 2)])
                .Select((at, rank) => (at, rank, scene)))
            .OrderBy(x => x.rank).ThenByDescending(x => x.scene.ToSeconds - x.scene.FromSeconds)
            .Select(x => new MontagePick(x.at, x.scene.Shows, true)).ToList();
    }

    /// <summary>
    /// The opening shots of a car clip. The first pick stays, because it was picked to show the creator. Then two
    /// moments of him at the wheel talking to the camera (<see cref="AtTheWheel"/>): the opening arrow points at
    /// "this 24 year old", and that is the one kind of shot sure to be him and not a friend or a fan beside the
    /// car. Which two depends on where the moment is, so that two clips from one video do not open on the same shot.
    /// Of the other picks, only those taken where a car is on screen. The list is then filled from the scenes
    /// themselves: first the scenes that show what the story is about (<see cref="Relevance"/>), all they have,
    /// then the others. Within each, the two best thumbnails of every scene before the third of any, and those
    /// before the moments taken every ten seconds through a scene; daylight before night, the outside of the car
    /// before its cabin, a scene's best thumbnail before its second, and a long scene before a passing one. Each
    /// is at least eight seconds from the others and outside the moment.
    /// </summary>
    /// <param name="story">The clip's title, meme line and narration. A clip about a Hellcat once opened on a red car
    /// parked in a lot, because that scene came first in the video.</param>
    public static IReadOnlyList<MontagePick> Montage(IReadOnlyList<MontagePick> picked, IReadOnlyList<CarScene> scenes,
        (double Start, double End) moment, int want = Candidates, string? story = null)
    {
        var result = new List<MontagePick>();
        bool Free(double at) => (at < moment.Start - 3 || at > moment.End + 3) && result.All(x => Math.Abs(x.AtSeconds - at) >= 8);
        if (picked.Count > 0 && Free(picked[0].AtSeconds)) result.Add(picked[0]);
        var wheel = AtTheWheel(scenes);
        var taken = 0;
        for (var index = 0; index < wheel.Count && taken < 2; index++)
        {
            var pick = wheel[((int)Math.Abs(moment.Start) + index) % wheel.Count];
            if (!Free(pick.AtSeconds)) continue;
            result.Add(pick);
            taken++;
        }
        for (var index = 1; index < picked.Count && result.Count < want; index++)
            if (At(scenes, picked[index].AtSeconds) is not null && Free(picked[index].AtSeconds))
                result.Add(picked[index]);

        // No more than a few from one scene: nine shots of the same car park at night made a clip that repeated itself.
        var fromScene = new Dictionary<CarScene, int>(ReferenceEqualityComparer.Instance);
        foreach (var (at, _, scene) in Offered(scenes, story))
        {
            if (result.Count >= want) break;
            if (fromScene.GetValueOrDefault(scene) >= PerScene || !Free(at)) continue;
            result.Add(new MontagePick(at, scene.Shows));
            fromScene[scene] = fromScene.GetValueOrDefault(scene) + 1;
        }
        return result;
    }

    /// <summary>The moments the scenes have to offer, best first: see <see cref="Montage"/> for the order.</summary>
    private static IEnumerable<(double At, int Rank, CarScene Scene)> Offered(IReadOnlyList<CarScene> scenes, string? story) =>
        scenes.Where(x => x.OnScreen).SelectMany(scene =>
            {
                IReadOnlyList<double> bests = scene.BestSeconds.Count > 0
                    ? scene.BestSeconds : [Math.Floor((scene.FromSeconds + scene.ToSeconds) / 2)];
                // A long scene has more to show than its best thumbnails: a moment every ten seconds through it.
                var more = Enumerable.Range(0, (int)Math.Max(0, (scene.ToSeconds - scene.FromSeconds - 5) / 10) + 1)
                    .Select(step => scene.FromSeconds + step * 10).Where(at => !bests.Contains(at));
                return bests.Select((at, rank) => (at, rank, scene)).Concat(more.Select((at, step) => (at, rank: MoreRank + step, scene)));
            })
            // The scenes of the story's own car first, all they have. A clip about a Hellcat was offered three shots
            // of its handover and six of other cars and other places, and too few of the nine were worth showing.
            .OrderBy(x => Relevance(story, x.scene) > 0 ? 0 : 1)
            .ThenBy(x => x.rank < 2 ? 0 : x.rank < MoreRank ? 1 : 2).ThenByDescending(x => Relevance(story, x.scene))
            .ThenBy(x => x.scene.Dark ? 1 : 0).ThenBy(x => x.scene.View == Interior ? 1 : 0).ThenBy(x => x.rank)
            .ThenByDescending(x => x.scene.ToSeconds - x.scene.FromSeconds).ThenBy(x => x.at);

    /// <summary>
    /// Moments sure to show the creator's face: him at the wheel (<see cref="AtTheWheel"/>), then the thumbnails a
    /// look for everything named as him facing the camera. A video with no car has only the second kind.
    /// </summary>
    public static IReadOnlyList<MontagePick> SurelyTheCreator(CarSceneScan scan) =>
    [
        .. AtTheWheel(scan.Scenes),
        .. (scan.CreatorSeconds ?? []).Select(at => new MontagePick(at, "the creator facing the camera", true)),
    ];

    /// <summary>
    /// The opening shots of an ordinary clip from a video whose picture has been looked at. A clip told what was
    /// said and showed whoever was talking; the other channel shows the car, the house and the money while its
    /// narrator talks. The first pick stays, and two moments sure to be the creator follow it, as in
    /// <see cref="Montage"/>. Then what the scenes have of the story's own subject (<see cref="Relevance"/>), then
    /// the rest of the picks, which were made knowing the scenes, and last the best thumbnail or two of every other
    /// scene: his wealth, whatever the story. Room is kept for three of the picks. Each is at least eight seconds
    /// from the others and outside the moment.
    /// </summary>
    public static IReadOnlyList<MontagePick> Showcase(IReadOnlyList<MontagePick> picked, CarSceneScan scan,
        (double Start, double End) moment, int want = Candidates, string? story = null)
    {
        var result = new List<MontagePick>();
        bool Free(double at) => (at < moment.Start - 3 || at > moment.End + 3) && result.All(x => Math.Abs(x.AtSeconds - at) >= 8);
        if (picked.Count > 0 && Free(picked[0].AtSeconds)) result.Add(picked[0]);
        var sure = SurelyTheCreator(scan);
        var taken = 0;
        for (var index = 0; index < sure.Count && taken < 2; index++)
        {
            var pick = sure[((int)Math.Abs(moment.Start) + index) % sure.Count];
            if (!Free(pick.AtSeconds)) continue;
            result.Add(pick);
            taken++;
        }
        var offered = Offered(scan.Scenes, story).ToList();
        var fromScene = new Dictionary<CarScene, int>(ReferenceEqualityComparer.Instance);
        void Fill(IEnumerable<(double At, int Rank, CarScene Scene)> from, int upTo)
        {
            foreach (var (at, _, scene) in from)
            {
                if (result.Count >= upTo) break;
                if (fromScene.GetValueOrDefault(scene) >= PerScene || !Free(at)) continue;
                result.Add(new MontagePick(at, scene.Shows));
                fromScene[scene] = fromScene.GetValueOrDefault(scene) + 1;
            }
        }
        Fill(offered.Where(x => Relevance(story, x.Scene) > 0), want - Math.Min(3, Math.Max(0, picked.Count - 1)));
        for (var index = 1; index < picked.Count && result.Count < want; index++)
            if (Free(picked[index].AtSeconds)) result.Add(picked[index]);
        Fill(offered.Where(x => Relevance(story, x.Scene) == 0 && x.Rank < 2), want);
        return result;
    }

    /// <summary>The words a story uses for each kind of thing a scene may show. A scene's own description need not
    /// share one with the story: "jet" is too short to count as a telling word, and a scene described as "a cabin
    /// with cream leather seats" shares nothing with "he chartered a private jet".</summary>
    private static readonly Dictionary<string, string[]> KindWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jet"] = ["jet", "jets", "plane", "flight", "flew", "fly", "flies", "flying", "helicopter"],
        ["yacht"] = ["yacht", "yachts", "boat"],
        ["watch"] = ["watch", "watches", "rolex", "patek", "audemars", "jewellery", "jewelry"],
        ["cash"] = ["cash", "money", "profit", "balance", "payout", "cheque", "check"],
        ["home"] = ["house", "home", "villa", "mansion", "penthouse", "pool", "backyard", "hotel", "suite"],
    };

    /// <summary>How well a scene suits a story: the telling words they share, and one more when the story speaks of
    /// the kind of thing the scene shows.</summary>
    public static int Relevance(string? story, CarScene scene) =>
        Relevance(story, scene.Shows) +
        (!string.IsNullOrWhiteSpace(story) && !scene.IsCar && KindWords.TryGetValue(scene.Kind, out var words) &&
         new string(story.Select(x => char.IsLetterOrDigit(x) ? char.ToLowerInvariant(x) : ' ').ToArray())
             .Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(words.Contains) ? 1 : 0);

    /// <summary>Where the moments taken through a scene rank, after every thumbnail the look at the video called best.</summary>
    private const int MoreRank = 100;

    /// <summary>How many of a clip's candidates one scene may give.</summary>
    public const int PerScene = 5;

    /// <summary>How many candidates a clip from a video that has been looked at is offered. A narration has room for
    /// seven shots and each plays once, and of twelve candidates the look at their frames left three to five: the
    /// same few then came round again under the narrator.</summary>
    public const int Candidates = 16;

    /// <summary>How many of the story's telling words a scene's description shares: a clip about "a free Hellcat"
    /// counts one for "the purple Hellcat at the dealership". Words every scene shares say nothing.</summary>
    public static int Relevance(string? story, string shows)
    {
        if (string.IsNullOrWhiteSpace(story)) return 0;
        var told = Telling(story);
        return Telling(shows).Count(told.Contains);
    }

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "creator", "trader", "this", "that", "with", "from", "into", "what", "when", "then", "they", "them", "their", "there",
        "have", "next", "year", "years", "about", "while", "talking", "camera", "drives", "driving", "wheel", "behind",
        "sits", "shown", "shows", "inside", "outside", "through", "another", "other", "does", "just", "over", "after",
        "before", "where", "which", "were", "been", "being", "gets", "gave", "gives", "made", "makes", "said", "says",
        "millionaire", "stranger", "person", "people", "video", "moment", "crazy", "insane",
    };

    private static HashSet<string> Telling(string text) =>
        new string(text.Select(x => char.IsLetterOrDigit(x) ? char.ToLowerInvariant(x) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 4 && !Common.Contains(x) && !x.All(char.IsDigit))
            .Select(x => x.Length > 4 && x.EndsWith('s') ? x[..^1] : x)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the window sits for a shot with a car in it. With no face it centres on the car. With a face it moves
    /// toward the car only as far as keeps the face inside, <paramref name="faceMargin"/> of the frame's width from
    /// the window's edge: the arrow and the skull are drawn on that face.
    /// </summary>
    public static double Frame(double? faceX, double? carX, double faceMargin = FaceMargin)
    {
        if (carX is not { } car || !double.IsFinite(car)) return faceX ?? 0.5;
        car = Math.Clamp(car, 0, 1);
        if (faceX is not { } face) return Math.Round(car, 4);
        var reach = Math.Max(0, Visible / 2 - faceMargin);
        return Math.Round(Math.Clamp(car, face - reach, face + reach), 4);
    }

    private static bool Number(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value);
    }

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;
}
