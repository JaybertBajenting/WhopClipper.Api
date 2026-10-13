using System.Text.Json;

namespace WhopClipper.Api.App.Services;

/// <summary>A moment elsewhere in the vlog the AI picked to show under the narration.</summary>
/// <param name="CreatorAtWheel">Taken from a long stretch of the creator at the wheel talking to the camera: the one
/// kind of shot known to be him, which a car clip opens on and the review recognises him by. In a video with no
/// such stretch, a thumbnail the look at the whole video named as him facing the camera
/// (<see cref="CarScenes.SurelyTheCreator"/>).</param>
public sealed record MontagePick(double AtSeconds, string Shows, bool CreatorAtWheel = false);

/// <summary>What the narrated style adds to a condensed story: the narrator's words, the meme line over the
/// window, and the montage moments.</summary>
public sealed record NarratedStory(CondensedStory Story, string Narration, string OverlayTitle, IReadOnlyList<MontagePick> Montage);

/// <summary>A point where a song slams back in: when, how big the rise is, and how loud the song is once it has.</summary>
public readonly record struct MusicDrop(double Seconds, double RiseDb, double LevelDb = -6);

/// <summary>One montage shot: which downloaded candidate, where in it, and for how long.</summary>
public sealed record MontageSlot(int Candidate, double InSeconds, double DurationSeconds);

public static class NarratedStories
{
    public const string Schema = """
    {"title":"WhopClipperNarratedStoriesV1","type":"object","additionalProperties":false,"required":["stories"],"properties":{"stories":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"required":["title","caption","overlayTitle","narration","teller","emphasis","score","rationale","pieces","montage"],"properties":{"title":{"type":"string"},"caption":{"type":"string"},"overlayTitle":{"type":"string"},"narration":{"type":"string"},"teller":{"type":"string"},"emphasis":{"type":"array","maxItems":5,"items":{"type":"string"}},"score":{"type":"number","minimum":0,"maximum":1},"rationale":{"type":"string"},"pieces":{"type":"array","minItems":1,"maxItems":6,"items":{"type":"object","additionalProperties":false,"required":["startSeconds","endSeconds","firstWords","lastWords"],"properties":{"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},"firstWords":{"type":"string"},"lastWords":{"type":"string"}}}},"montage":{"type":"array","minItems":3,"maxItems":10,"items":{"type":"object","additionalProperties":false,"required":["atSeconds","shows"],"properties":{"atSeconds":{"type":"number","minimum":0},"shows":{"type":"string"}}}}}}}}}
    """;

    public const string ReviewSchema = """
    {"title":"WhopClipperMontageReviewV1","type":"object","additionalProperties":false,"required":["framesInspected","shots","slots"],"properties":{"framesInspected":{"type":"boolean"},"shots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["label","approved","weak","weakFrames","showsPerson","visibleContent","reason"],"properties":{"label":{"type":"string"},"approved":{"type":"boolean"},"weak":{"type":"boolean"},"weakFrames":{"type":"array","maxItems":3,"items":{"type":"integer","minimum":1,"maximum":3}},"showsPerson":{"type":"boolean"},"visibleContent":{"type":"string"},"reason":{"type":"string"}}}},"slots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["slot","label"],"properties":{"slot":{"type":"string"},"label":{"type":["string","null"]}}}}}}
    """;

    /// <summary>
    /// The shots a clip may show. A weak shot (a corner of the car at the frame's edge, someone who has nothing to do
    /// with the story) is left out, however clean it is. Only when fewer than <paramref name="minimum"/> strong
    /// shots are left do the weak ones come back, after the strong ones, so the montage still has something to cut to.
    /// </summary>
    public static IReadOnlyList<T> WithoutWeak<T>(IReadOnlyList<(T Shot, bool Weak)> shots, int minimum = 3)
    {
        var strong = shots.Where(x => !x.Weak).Select(x => x.Shot).ToList();
        return strong.Count >= minimum ? strong : [.. strong, .. shots.Where(x => x.Weak).Select(x => x.Shot)];
    }

    /// <summary>
    /// The longest stretch of a candidate that is one shot. A few seconds taken out of a vlog often straddle one of
    /// the vlog's own cuts; such a candidate showed a grey Lamborghini while the narrator said "Hellcat", because its
    /// last frame was the Hellcat. A tenth of a second is kept clear of each cut. Of two stretches alike, the earlier.
    /// </summary>
    /// <param name="cuts">Where the file jumps to another shot, in seconds from its start (<c>narrated.py cuts</c>).</param>
    public static (double InSeconds, double LengthSeconds) LongestShot(double lengthSeconds, IReadOnlyList<double> cuts,
        double guardSeconds = 0.1)
    {
        double[] edges = [0, .. cuts.Where(x => x > 0 && x < lengthSeconds).Order(), lengthSeconds];
        var best = 0;
        for (var index = 1; index + 1 < edges.Length; index++)
            if (edges[index + 1] - edges[index] > edges[best + 1] - edges[best] + 0.001) best = index;
        var start = edges[best] + (best == 0 ? 0 : guardSeconds);
        var end = edges[best + 1] - (best + 2 == edges.Length ? 0 : guardSeconds);
        return (Math.Round(start, 3), Math.Round(Math.Max(0, end - start), 3));
    }

    /// <summary>How much of a frame's width the window shows.</summary>
    public const double WindowShare = FfmpegArgumentBuilder.NarratedVisibleWidth / 1920d;

    /// <summary>
    /// Where the window's centre goes so that it stays inside a picture that does not fill the frame: phone footage
    /// dropped into a vlog sits between black bars, and a window centred on a car near its edge showed a bar.
    /// </summary>
    /// <param name="left">Where the picture starts across the frame, 0 to 1.</param>
    /// <param name="right">Where it ends.</param>
    public static double KeepInPicture(double focusX, double left, double right) =>
        right - left <= WindowShare
            ? Math.Round((left + right) / 2, 4)
            : Math.Round(Math.Clamp(focusX, left + WindowShare / 2, right - WindowShare / 2), 4);

    /// <summary>When in a candidate the three frames the review looks at are taken: near its start, its middle and
    /// near its end.</summary>
    public static IReadOnlyList<double> ReviewFrameTimes(double lengthSeconds) =>
        [0.3, lengthSeconds / 2, Math.Max(0.4, lengthSeconds - 0.4)];

    /// <summary>
    /// The part of a candidate worth showing, when the review found some of its three frames weak: a few seconds of
    /// vlog often start on the car and swing away to something else, or begin on the tail of another shot. Nobody
    /// looked at what lies between a strong frame and a weak one (a guess at the midpoint once showed half a second
    /// of a night scene), so the part kept runs only between two strong frames side by side, out to the end of the
    /// candidate on their side. Null when there are no two such frames: the candidate is weak.
    /// </summary>
    /// <param name="weakFrames">The weak frames, counted 1 to 3 from the candidate's start.</param>
    public static (double InSeconds, double LengthSeconds)? StrongPart(double lengthSeconds, IReadOnlyCollection<int> weakFrames)
    {
        if (!weakFrames.Any(x => x is >= 1 and <= 3)) return (0, Math.Round(lengthSeconds, 3));
        if (weakFrames.Contains(2) || weakFrames.Contains(1) && weakFrames.Contains(3)) return null;
        var middle = ReviewFrameTimes(lengthSeconds)[1];
        var (start, length) = weakFrames.Contains(1) ? (middle, lengthSeconds - middle) : (0d, middle);
        return length >= 1 ? (Math.Round(start, 3), Math.Round(length, 3)) : null;
    }

    /// <summary>The frames of a row the review marked weak, as numbers 1 to 3; anything else it wrote is ignored.</summary>
    public static IReadOnlyCollection<int> ParseWeakFrames(JsonElement shot)
    {
        var result = new HashSet<int>();
        if (shot.ValueKind == JsonValueKind.Object && shot.TryGetProperty("weakFrames", out var frames) &&
            frames.ValueKind == JsonValueKind.Array)
            foreach (var frame in frames.EnumerateArray())
                if (frame.ValueKind == JsonValueKind.Number && frame.TryGetInt32(out var number) && number is >= 1 and <= 3)
                    result.Add(number);
        return result;
    }

    /// <summary>The row the review chose for each slot ("S2" a beat of the narration, "C1" a cutaway), by slot name.
    /// A slot it left out, or answered with null, is not in the result.</summary>
    public static IReadOnlyDictionary<string, string> ParseSlots(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("slots", out var slots) ||
            slots.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in slots.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("slot", out var slot) &&
                slot.ValueKind == JsonValueKind.String && item.TryGetProperty("label", out var label) &&
                label.ValueKind == JsonValueKind.String && slot.GetString()?.Trim() is { Length: > 0 } name &&
                label.GetString()?.Trim() is { Length: > 0 } row)
                result.TryAdd(name, row);
        return result;
    }

    /// <summary>Narration a narrator reads in about 5-13 seconds, as the reference's run.</summary>
    public const int MinimumNarrationWords = 12;
    public const int MaximumNarrationWords = 40;

    /// <summary>
    /// Reads the AI's stories: the payoff pieces through the podcast parser (which snaps cuts onto the quoted words and
    /// enforces 10-60 seconds), and the narration, meme line and montage beside them. A story without usable
    /// narration or montage is dropped rather than half-built.
    /// </summary>
    /// <param name="minimumSeconds">The shortest moment kept. A car clip may be one short line.</param>
    /// <param name="carScenes">Set for a car-clips run. A moment that does not play while a car is on screen is
    /// dropped, and the opening shots are taken from where the car is.</param>
    /// <param name="shown">Set for an ordinary run whose video has been looked at. Every moment is kept; its opening
    /// shots are joined by what the video has to show (<see cref="CarScenes.Showcase"/>).</param>
    public static IReadOnlyList<NarratedStory> Parse(JsonElement root, double? episodeSeconds, IReadOnlyList<TranscriptWord> words,
        double minimumSeconds = StoryParser.MinimumSeconds, IReadOnlyList<CarScene>? carScenes = null, CarSceneScan? shown = null)
    {
        var condensed = StoryParser.ParseStories(root, episodeSeconds, words, minimumSeconds);
        var extras = new Dictionary<string, (string Narration, string Overlay, List<MontagePick> Montage)>(StringComparer.Ordinal);
        if (root.TryGetProperty("stories", out var stories) && stories.ValueKind == JsonValueKind.Array)
            foreach (var item in stories.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? AiOutputParser.CleanHookText(t.GetString() ?? "") : "";
                var narration = item.TryGetProperty("narration", out var n) ? CleanNarration(n.GetString() ?? "") : "";
                var overlay = item.TryGetProperty("overlayTitle", out var o) ? AiOutputParser.CleanHookText(o.GetString() ?? "") : "";
                var montage = new List<MontagePick>();
                if (item.TryGetProperty("montage", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var pick in list.EnumerateArray())
                        if (pick.TryGetProperty("atSeconds", out var at) && at.TryGetDouble(out var seconds) && seconds >= 0 &&
                            (episodeSeconds is null || seconds < episodeSeconds.Value - 2))
                            montage.Add(new MontagePick(seconds, pick.TryGetProperty("shows", out var shows) ? shows.GetString() ?? "" : ""));
                if (title.Length > 0) extras.TryAdd(title, (narration, overlay, montage));
            }
        var result = new List<NarratedStory>();
        foreach (var story in condensed)
        {
            if (!extras.TryGetValue(story.Title, out var extra)) continue;
            var count = extra.Narration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (count is < MinimumNarrationWords or > MaximumNarrationWords || extra.Overlay.Length is < 3 or > 70) continue;
            // Montage moments inside the payoff would show the same seconds twice.
            var start = story.Pieces.Min(x => x.Start) - 3;
            var end = story.Pieces.Max(x => x.End) + 3;
            IReadOnlyList<MontagePick> montage = extra.Montage.Where(x => x.AtSeconds < start || x.AtSeconds > end).ToList();
            if (carScenes is not null)
            {
                if (!CarScenes.IsCarMoment(story.Pieces, carScenes)) continue;
                montage = CarScenes.Montage(montage, carScenes, (start + 3, end - 3),
                    story: $"{story.Title} {extra.Overlay} {extra.Narration}");
            }
            else if (shown is not null)
                montage = CarScenes.Showcase(montage, shown, (start + 3, end - 3),
                    story: $"{story.Title} {extra.Overlay} {extra.Narration}");
            if (montage.Count < 2) continue;
            result.Add(new NarratedStory(story, extra.Narration, extra.Overlay, montage));
        }
        return result;
    }

    /// <summary>What a writer likes to call a creator who owns several cars, and the root the video would have to
    /// use for it to be true of him ("my collection", "I collect").</summary>
    private static readonly (string Label, string Root)[] Labels = [("collector", "collect")];

    /// <summary>
    /// The label a story gives the creator that the video never uses, or null. "This car collector" was said of a
    /// trader in three clips from a vlog where neither he nor its title speaks of collecting anything.
    /// </summary>
    /// <param name="said">Everything the video says: its transcript and its title.</param>
    /// <param name="written">What the story says of him: its narration, title, caption and meme line.</param>
    public static string? UnsaidLabel(string said, params string?[] written)
    {
        static IEnumerable<string> Tokens(string? text) =>
            new string((text ?? "").Select(x => char.IsLetter(x) ? char.ToLowerInvariant(x) : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var heard = Tokens(said).ToHashSet();
        return Labels.Where(x => written.Any(text => Tokens(text).Any(word => word.StartsWith(x.Label, StringComparison.Ordinal))) &&
                                 !heard.Any(word => word.StartsWith(x.Root, StringComparison.Ordinal)))
            .Select(x => x.Label).FirstOrDefault();
    }

    /// <summary>Plain spoken text: no quotes, no stage directions, no line breaks, no dashes a voice would read out.</summary>
    public static string CleanNarration(string text)
    {
        var cleaned = text.Replace('\n', ' ').Replace('\r', ' ').Replace("\"", "").Replace("“", "").Replace("”", "")
            .Replace(" - ", ", ").Replace(" — ", ", ").Replace("—", ", ").Replace(" – ", ", ");
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>
    /// Spreads the montage across the narration. The first shot runs through the window's opening (about 1.75 s);
    /// the rest cut roughly every <paramref name="shotSeconds"/>, each cut moved onto the nearest word start so a
    /// cut never lands mid-word. No candidate is shown twice while one that has not been shown is left: the same
    /// few shots coming round again under the narrator lost the viewer. With fewer candidates than shots, the
    /// shots run longer instead, up to <see cref="LongestShotSeconds"/>; only when that is still not enough is a
    /// candidate used again, from a later point in it.
    /// </summary>
    /// <param name="asked">Which candidate each shot should show, when something looked at the pictures and matched
    /// them to the narrator's words; see <see cref="Arrange"/>. Counted on the cuts <see cref="Cuts"/> gives for
    /// <paramref name="shotSeconds"/>.</param>
    /// <param name="fill">The candidates that fill the shots nothing was asked for, best first.</param>
    public static IReadOnlyList<MontageSlot> Allocate(double narrationSeconds, IReadOnlyList<TranscriptWord> narrationWords,
        IReadOnlyList<double> candidateSeconds, double shotSeconds = 1.2, double firstSeconds = 1.75,
        IReadOnlyList<int?>? asked = null, IReadOnlyList<int>? fill = null)
    {
        if (candidateSeconds.Count == 0 || narrationSeconds <= 0) return [];
        var asking = Cuts(narrationSeconds, narrationWords, shotSeconds, firstSeconds);
        var cuts = asking;
        // What there is to show: the opener, the candidates that fill, and any other that was asked for.
        var playable = (fill ?? Enumerable.Range(0, candidateSeconds.Count)).Concat((asked ?? []).OfType<int>()).Append(0)
            .Where(x => x >= 0 && x < candidateSeconds.Count).Distinct().ToList();
        var shots = asking.Count - 2;
        var rest = narrationSeconds - Math.Min(firstSeconds, narrationSeconds);
        if (shots > playable.Count - 1 && rest >= 0.5)
        {
            // As few shots as there are candidates for, but none so long that it runs past the part of its file
            // that was looked at: a cut may move a third of a second onto a word.
            var longest = Math.Max(shotSeconds, Math.Min(LongestShotSeconds, playable.Min(x => candidateSeconds[x]) - 0.35));
            var fewest = Math.Max(1, Math.Min(shots, (int)Math.Ceiling(rest / longest)));
            var count = Math.Clamp(playable.Count - 1, fewest, shots);
            if (count < shots) cuts = Cuts(narrationSeconds, narrationWords, rest / count, firstSeconds);
        }
        // A candidate too short to show a fresh part the second time comes round once only.
        var shown = Arrange(cuts.Count - 1, candidateSeconds.Count, ReferenceEquals(cuts, asking) ? asked : Regroup(asked, asking, cuts),
            fill, candidateSeconds.Select(x => x >= 2 * ReuseStepSeconds ? 2 : 1).ToList());
        var slots = new List<MontageSlot>();
        var uses = new int[candidateSeconds.Count];
        for (var index = 0; index + 1 < cuts.Count; index++)
        {
            var length = Math.Round(cuts[index + 1] - cuts[index], 3);
            var candidate = shown[index];
            // A reused candidate starts after the part already shown, when it is long enough to have one.
            var inPoint = uses[candidate] * ReuseStepSeconds;
            if (inPoint + length > candidateSeconds[candidate]) inPoint = 0;
            uses[candidate]++;
            slots.Add(new MontageSlot(candidate, Math.Round(inPoint, 3), length));
        }
        return slots;
    }

    /// <summary>How much later a candidate starts each time it is shown again.</summary>
    public const double ReuseStepSeconds = 1.2;

    /// <summary>How long a shot may run so that none has to play twice. The reference cuts every 1.2 seconds; a
    /// shot held for two is slower, and still better than one the viewer has just seen.</summary>
    public const double LongestShotSeconds = 2.0;

    /// <summary>
    /// What is asked of fewer, longer shots: each takes the place of the shots whose middle falls in it, and of the
    /// candidates asked for those it shows the first that no earlier shot has taken. The first shot is the opener
    /// whatever was asked for it.
    /// </summary>
    /// <param name="asking">The cuts <paramref name="asked"/> was counted on.</param>
    private static IReadOnlyList<int?>? Regroup(IReadOnlyList<int?>? asked, IReadOnlyList<double> asking, IReadOnlyList<double> cuts)
    {
        if (asked is null) return null;
        var taken = new HashSet<int> { 0 };
        var result = new List<int?> { null };
        for (var shot = 1; shot + 1 < cuts.Count; shot++)
        {
            int? choice = null;
            for (var old = 1; old + 1 < asking.Count && old < asked.Count && choice is null; old++)
            {
                var middle = (asking[old] + asking[old + 1]) / 2;
                if (middle >= cuts[shot] && middle < cuts[shot + 1] && asked[old] is { } wanted && taken.Add(wanted)) choice = wanted;
            }
            result.Add(choice);
        }
        return result;
    }

    /// <summary>
    /// Where the picture cuts under the narration, from 0 to its end: the first shot runs through the window's
    /// opening, the rest cut roughly every <paramref name="shotSeconds"/>, each on the nearest word start.
    /// </summary>
    public static IReadOnlyList<double> Cuts(double narrationSeconds, IReadOnlyList<TranscriptWord> narrationWords,
        double shotSeconds = 1.2, double firstSeconds = 1.75)
    {
        if (narrationSeconds <= 0) return [];
        firstSeconds = Math.Min(firstSeconds, narrationSeconds);
        var rest = narrationSeconds - firstSeconds;
        var count = rest < 0.5 ? 0 : Math.Max(1, (int)Math.Round(rest / shotSeconds));
        var cuts = new List<double> { 0, firstSeconds };
        var wordStarts = narrationWords.Select(x => x.Start).Where(x => x > firstSeconds + 0.5 && x < narrationSeconds - 0.5).ToList();
        for (var index = 1; index < count; index++)
        {
            var ideal = firstSeconds + rest * index / count;
            var snapped = wordStarts.Where(x => Math.Abs(x - ideal) <= 0.35).OrderBy(x => Math.Abs(x - ideal)).FirstOrDefault(ideal);
            if (snapped - cuts[^1] >= 0.6 && narrationSeconds - snapped >= 0.6) cuts.Add(snapped);
        }
        if (count > 0) cuts.Add(narrationSeconds);
        else cuts[^1] = narrationSeconds;
        return cuts;
    }

    /// <summary>What the narrator says during each shot: the words that start between one cut and the next.</summary>
    public static IReadOnlyList<string> Beats(IReadOnlyList<double> cuts, IReadOnlyList<TranscriptWord> words)
    {
        var beats = new List<string>();
        for (var index = 0; index + 1 < cuts.Count; index++)
        {
            var (from, to) = (cuts[index], index + 2 == cuts.Count ? double.MaxValue : cuts[index + 1]);
            beats.Add(string.Join(' ', words.Where(x => x.Start >= from - 0.001 && x.Start < to - 0.001).Select(x => x.Word)));
        }
        return beats;
    }

    /// <summary>
    /// Which candidate each shot shows. The first is always candidate 0, the opening shot. After that a shot shows
    /// what was asked for it, so the picture follows the narrator's words ("a free Hellcat" over the Hellcat, not
    /// over a face); the shots nothing was asked for take the candidates of <paramref name="fill"/> in its order,
    /// and after those the ones outside it. No candidate is shown again while one that has not been shown is left.
    /// When every one has been shown, the least shown comes round again, never twice running.
    /// </summary>
    /// <param name="most">How often each candidate may be shown when they do come round again, when not twice:
    /// once for one with no second part to show.</param>
    public static IReadOnlyList<int> Arrange(int shots, int candidates, IReadOnlyList<int?>? asked = null,
        IReadOnlyList<int>? fill = null, IReadOnlyList<int>? most = null)
    {
        if (shots <= 0 || candidates <= 0) return [];
        var order = (fill ?? []).Where(x => x >= 0 && x < candidates).Distinct().ToList();
        if (order.Count == 0) order = Enumerable.Range(0, candidates).ToList();
        // A candidate left out of the fill still beats a second look at one that is in it.
        order.AddRange(Enumerable.Range(1, candidates - 1).Where(x => !order.Contains(x)).ToList());
        int? Asked(int shot) => asked is not null && shot < asked.Count && asked[shot] is { } wanted && wanted >= 0 &&
                                wanted < candidates ? wanted : null;
        int Most(int candidate) => most is not null && candidate < most.Count ? Math.Max(1, most[candidate]) : 2;
        var uses = new int[candidates];
        var result = new List<int>();
        for (var shot = 0; shot < shots; shot++)
        {
            var previous = shot == 0 ? -1 : result[^1];
            int candidate;
            if (shot == 0) candidate = 0;
            else if (Asked(shot) is { } wanted && wanted != previous && uses[wanted] == 0) candidate = wanted;
            else
            {
                // Not one that a later shot asks for, while another is left: that shot would then go without it.
                var later = Enumerable.Range(shot + 1, shots - shot - 1).Select(Asked).OfType<int>().ToHashSet();
                candidate = order.OrderBy(x => x == previous ? 4 : uses[x] >= Most(x) ? 3 : uses[x] > 0 ? 2 : later.Contains(x) ? 1 : 0)
                    .ThenBy(x => uses[x]).First();
            }
            uses[candidate]++;
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// Stretches of the moment where the picture may leave the speaker and show what he is talking about while his
    /// voice carries on, as the reference's payoff does. The first and last second and a half stay on him: the viewer
    /// has to see who is talking, and the clip freezes on his face. Each stretch starts and ends on a word.
    /// </summary>
    /// <param name="wordStarts">When each word of the moment starts, on the moment's own clock.</param>
    public static IReadOnlyList<(double Start, double Seconds)> CutawayWindows(double momentSeconds, IReadOnlyList<double> wordStarts,
        double leadSeconds = 1.5, double tailSeconds = 1.5, double lengthSeconds = 1.6, double gapSeconds = 1.5)
    {
        var result = new List<(double Start, double Seconds)>();
        var last = momentSeconds - tailSeconds;
        double Snap(double ideal) => wordStarts.Where(x => Math.Abs(x - ideal) <= 0.35).OrderBy(x => Math.Abs(x - ideal)).FirstOrDefault(ideal);
        var clock = leadSeconds;
        while (clock + 1 <= last && result.Count < 6)
        {
            var start = Math.Max(1, Snap(clock));
            var end = Math.Min(last, Snap(start + lengthSeconds));
            if (end - start < 1) break;
            result.Add((Math.Round(start, 3), Math.Round(end - start, 3)));
            clock = end + gapSeconds;
        }
        return result;
    }

    /// <summary>
    /// Where the song should start so that the clip's last word lands on a drop, which is how @mambafx.luxury times
    /// every short: Canto de Luna fades almost to silence, slams back at 53 seconds, and the 0.6 s skull freeze is
    /// that slam. Of the drops late enough to leave room for the speech, the one nearest where the segment guide
    /// says the reference's clips end is used; without a guide (or with none near it), the biggest rise. Null when
    /// the clip is longer than every drop is late, so the caller falls back to the song's usual start.
    /// </summary>
    /// <param name="drops">The song's rises, from <c>narrated.py drops</c>.</param>
    /// <param name="guideEndSeconds">Where the reference's clips end in this song, from the segment guide.</param>
    public static MusicDrop? PickDrop(IReadOnlyList<MusicDrop> drops, double speechSeconds, double? guideEndSeconds,
        double freezeSeconds)
    {
        var reachable = drops.Where(x => x.Seconds >= speechSeconds).ToList();
        if (reachable.Count == 0) return null;
        var near = guideEndSeconds is { } end
            ? reachable.Where(x => Math.Abs(x.Seconds - (end - freezeSeconds)) <= 12).ToList() : [];
        return (near.Count > 0 ? near : reachable).MaxBy(x => x.RiseDb);
    }

    /// <summary>Where the song starts so the speech ends on <see cref="PickDrop"/>'s drop; null when none fits.</summary>
    public static double? MusicStartForDrop(IReadOnlyList<MusicDrop> drops, double speechSeconds, double? guideEndSeconds,
        double freezeSeconds) =>
        PickDrop(drops, speechSeconds, guideEndSeconds, freezeSeconds) is { } drop ? Math.Round(drop.Seconds - speechSeconds, 3) : null;

    /// <summary>The reference's drop plays at about -12 dB RMS, whatever the song.</summary>
    public const double DropLevelDb = -12;

    /// <summary>
    /// The gain that plays a song's drop at the reference's loudness. Songs are mastered differently after their drop:
    /// one version of Canto de Luna sits at -4.5 dB and another at -10.5, so one fixed gain left the second 6 dB soft
    /// and its ending flat. <paramref name="musicVolume"/> is the campaign's slider, where 0.4 means "as the
    /// reference"; it scales the result.
    /// </summary>
    public static double DropGain(double dropLevelDb, double musicVolume) =>
        Math.Round(Math.Clamp(Math.Pow(10, (DropLevelDb - dropLevelDb) / 20) * (musicVolume / 0.4), 0.05, 1.5), 3);

    /// <summary>
    /// A shot's focus after moving its window sideways by <paramref name="shift"/> of the frame's width (positive is
    /// right). Counted from where the window really sits, not from the stored focus: a subject near the edge has a
    /// window already pinned to that edge, and nudging its focus alone would move nothing.
    /// </summary>
    public static double ShiftFocus(double focusX, double shift) =>
        Math.Round(Math.Clamp((FfmpegArgumentBuilder.NarratedCropX(focusX) + FfmpegArgumentBuilder.NarratedVisibleWidth / 2d) / 1920
                              + shift, 0, 1), 4);

    /// <summary>
    /// The library file for one of the two sounds: the first whose name has one of <paramref name="words"/>, trying
    /// the words in order, so "Camera Shutter" is taken before a "Camera Whoosh". Files come newest folder first.
    /// </summary>
    public static string? PickSound(IReadOnlyList<string> paths, params string[] words) =>
        words.Select(word => paths.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(path => path is not null);

    /// <summary>
    /// When the camera shutter starts: a frame ahead of the hand-over, so its click is already sounding as the white
    /// flash appears. The flash is the first 0.12 s of the moment itself, which begins where the narration ends.
    /// </summary>
    public static double ShutterAt(double narrationSeconds) => Math.Round(Math.Max(0, narrationSeconds - 0.03), 3);

    /// <summary>
    /// Word timings when the narration has no timestamps of its own (the Windows test voice): each word gets a share of
    /// the audio proportional to its length, which is close enough for one-word captions.
    /// </summary>
    public static IReadOnlyList<TranscriptWord> SpreadWords(string text, double durationSeconds, double leadSeconds = 0.1)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || durationSeconds <= leadSeconds) return [];
        var weights = words.Select(x => Math.Max(2, x.Length) + 1.0).ToList();
        var span = durationSeconds - leadSeconds * 2;
        var clock = leadSeconds;
        var result = new List<TranscriptWord>();
        for (var index = 0; index < words.Length; index++)
        {
            var length = span * weights[index] / weights.Sum();
            result.Add(new TranscriptWord(Math.Round(clock, 3), Math.Round(clock + length * 0.9, 3), words[index]));
            clock += length;
        }
        return result;
    }
}
