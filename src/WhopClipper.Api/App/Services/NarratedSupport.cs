using System.Text.Json;

namespace WhopClipper.Api.App.Services;

/// <summary>A moment elsewhere in the vlog the AI picked to show under the narration.</summary>
public sealed record MontagePick(double AtSeconds, string Shows);

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
    {"title":"WhopClipperMontageReviewV1","type":"object","additionalProperties":false,"required":["framesInspected","shots"],"properties":{"framesInspected":{"type":"boolean"},"shots":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["label","approved","showsPerson","visibleContent","reason"],"properties":{"label":{"type":"string"},"approved":{"type":"boolean"},"showsPerson":{"type":"boolean"},"visibleContent":{"type":"string"},"reason":{"type":"string"}}}}}}
    """;

    /// <summary>Narration a narrator reads in about 5-13 seconds, as the reference's run.</summary>
    public const int MinimumNarrationWords = 12;
    public const int MaximumNarrationWords = 40;

    /// <summary>
    /// Reads the AI's stories: the payoff pieces through the podcast parser (which snaps cuts onto the quoted words and
    /// enforces 10-60 seconds), and the narration, meme line and montage beside them. A story without usable
    /// narration or montage is dropped rather than half-built.
    /// </summary>
    public static IReadOnlyList<NarratedStory> Parse(JsonElement root, double? episodeSeconds, IReadOnlyList<TranscriptWord> words)
    {
        var condensed = StoryParser.ParseStories(root, episodeSeconds, words);
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
            var montage = extra.Montage.Where(x => x.AtSeconds < start || x.AtSeconds > end).ToList();
            if (montage.Count < 2) continue;
            result.Add(new NarratedStory(story, extra.Narration, extra.Overlay, montage));
        }
        return result;
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
    /// cut never lands mid-word. Candidates are used in order; when there are more shots than candidates, a
    /// candidate is used again from a later point in it.
    /// </summary>
    public static IReadOnlyList<MontageSlot> Allocate(double narrationSeconds, IReadOnlyList<TranscriptWord> narrationWords,
        IReadOnlyList<double> candidateSeconds, double shotSeconds = 1.2, double firstSeconds = 1.75)
    {
        if (candidateSeconds.Count == 0 || narrationSeconds <= 0) return [];
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

        var slots = new List<MontageSlot>();
        var uses = new int[candidateSeconds.Count];
        for (var index = 0; index + 1 < cuts.Count; index++)
        {
            var length = Math.Round(cuts[index + 1] - cuts[index], 3);
            var candidate = index % candidateSeconds.Count;
            // A reused candidate starts after the part already shown, when it is long enough to have one.
            var inPoint = uses[candidate] * 1.2;
            if (inPoint + length > candidateSeconds[candidate]) inPoint = 0;
            uses[candidate]++;
            slots.Add(new MontageSlot(candidate, Math.Round(inPoint, 3), length));
        }
        return slots;
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
