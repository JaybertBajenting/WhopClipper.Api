using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Reads the per-word "en-orig" json3 caption track YouTube generates for an episode. Each segment
/// carries its own offset, so a whole hour's words arrive timed without downloading or transcribing
/// the video - which matters when the drive cannot hold the episode at all.
/// </summary>
public static class YouTubeCaptions
{
    /// <summary>Whether yt-dlp failed because YouTube is throttling this PC ("HTTP Error 429: Too Many Requests"),
    /// which passes with time, rather than because the video has no captions.</summary>
    public static bool IsRateLimited(string? standardError) =>
        standardError is not null &&
        (standardError.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase));

    /// <summary>The track has start times only. A word lasts until the next begins, capped so a
    /// long pause does not stretch one word across it.</summary>
    public static WordTranscript ToTranscript(string json3, double maxWordSeconds = 0.8)
    {
        using var document = JsonDocument.Parse(json3);
        var starts = new List<(double Start, string Word)>();
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in events.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("segs", out var segments) ||
                    segments.ValueKind != JsonValueKind.Array) continue;
                var eventStart = entry.TryGetProperty("tStartMs", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var ms) ? ms : 0;
                foreach (var segment in segments.EnumerateArray())
                {
                    var text = segment.TryGetProperty("utf8", out var utf8) ? utf8.GetString() ?? string.Empty : string.Empty;
                    var offset = segment.TryGetProperty("tOffsetMs", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetDouble(out var oms) ? oms : 0;
                    foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // [Music], [ __ ] for a bleeped word, and >> for a change of speaker.
                        if (token.StartsWith('[') || token.EndsWith(']') || token.All(c => c is '_' or '>')) continue;
                        starts.Add(((eventStart + offset) / 1000.0, token));
                    }
                }
            }
        }

        var ordered = starts.OrderBy(x => x.Start).ToList();
        var words = new List<TranscriptWord>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var start = ordered[index].Start;
            var end = index + 1 < ordered.Count
                ? Math.Min(ordered[index + 1].Start, start + maxWordSeconds)
                : start + maxWordSeconds;
            if (end <= start) end = start + 0.05;
            words.Add(new TranscriptWord(start, end, ordered[index].Word));
        }
        return new WordTranscript("en", words.Count == 0 ? 0 : words[^1].End, words);
    }
}

/// <summary>
/// Works out where a downloaded section sits on the episode clock. A section fetched with
/// --download-sections starts on a keyframe, not on the second asked for, so its own time zero can
/// be a few seconds off. Matching three-word phrases between the episode captions and the
/// section's own transcript gives the true offset, whatever the keyframe did.
/// </summary>
public static class TranscriptAligner
{
    public static string Normalise(string word) => new(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <returns>Seconds to add to a local time to reach the reference clock, or null when too few
    /// phrases match to trust.</returns>
    public static (double Offset, int Matches)? EstimateOffset(IReadOnlyList<TranscriptWord> reference,
        IReadOnlyList<TranscriptWord> local, int gram = 3)
    {
        var referenceTokens = reference.Select(x => (Key: Normalise(x.Word), x.Start)).Where(x => x.Key.Length > 0).ToList();
        var localTokens = local.Select(x => (Key: Normalise(x.Word), x.Start)).Where(x => x.Key.Length > 0).ToList();
        var index = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        for (var i = 0; i + gram <= referenceTokens.Count; i++)
        {
            var key = string.Join(' ', referenceTokens.Skip(i).Take(gram).Select(x => x.Key));
            if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add(referenceTokens[i].Start);
        }

        var differences = new List<double>();
        for (var i = 0; i + gram <= localTokens.Count; i++)
        {
            var key = string.Join(' ', localTokens.Skip(i).Take(gram).Select(x => x.Key));
            // Only phrases said once: "you know what" matches everywhere and proves nothing.
            if (index.TryGetValue(key, out var hits) && hits.Count == 1)
                differences.Add(hits[0] - localTokens[i].Start);
        }
        if (differences.Count < 5) return null;

        differences.Sort();
        var median = differences[differences.Count / 2];
        var agreeing = differences.Where(x => Math.Abs(x - median) <= 1.5).OrderBy(x => x).ToList();
        return agreeing.Count < 5 ? null : (agreeing[agreeing.Count / 2], agreeing.Count);
    }
}

/// <summary>
/// Pulls each story piece onto the words actually spoken inside it, so a cut never opens on the
/// tail of a word or clips the first syllable of the next one.
/// </summary>
public static class PieceSnapper
{
    public static IReadOnlyList<(double Start, double End)> Snap(IEnumerable<(double Start, double End)> pieces,
        IReadOnlyList<TranscriptWord> words, double mediaSeconds)
    {
        var ordered = words.OrderBy(x => x.Start).ToList();
        var snapped = new List<(double Start, double End)>();
        foreach (var (start, end) in pieces)
        {
            var first = ordered.FindIndex(x => x.Start >= start - 0.15 && x.Start < end - 0.05);
            if (first < 0) continue;
            var last = ordered.FindLastIndex(x => x.Start >= start - 0.15 && x.Start < end - 0.05 && x.End <= end + 0.35);
            if (last < first) last = first;
            var previousEnd = first > 0 ? ordered[first - 1].End : 0;
            var nextStart = last + 1 < ordered.Count ? ordered[last + 1].Start : mediaSeconds;
            var cutIn = Math.Max(previousEnd, ordered[first].Start - 0.08);
            var cutOut = Math.Min(Math.Min(nextStart, mediaSeconds), ordered[last].End + 0.12);
            if (cutOut - cutIn >= 0.25) snapped.Add((Math.Max(0, cutIn), cutOut));
        }
        return StoryParser.Merge(snapped);
    }

    /// <summary>
    /// The reference channel leaves no pauses: across 31 of its shorts the longest gap between words is a
    /// median 0.36 s, and none holds a silence over half a second. Ours spent 8% of each clip in pauses.
    /// A silence inside a piece longer than <paramref name="maxPause"/> is cut down to <paramref name="keep"/>.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> TightenPauses(IReadOnlyList<(double Start, double End)> pieces,
        IReadOnlyList<TranscriptWord> words, double maxPause = 0.4, double keep = 0.2)
    {
        var ordered = words.OrderBy(x => x.Start).ToList();
        var result = new List<(double Start, double End)>();
        foreach (var (start, end) in pieces)
        {
            var inside = ordered.Where(x => x.Start >= start && x.End <= end + 0.05).ToList();
            var cutIn = start;
            for (var index = 0; index + 1 < inside.Count; index++)
            {
                if (inside[index + 1].Start - inside[index].End <= maxPause) continue;
                result.Add((cutIn, inside[index].End + keep / 2));
                cutIn = inside[index + 1].Start - keep / 2;
            }
            result.Add((cutIn, end));
        }
        return result.Where(x => x.End - x.Start >= 0.25).ToList();
    }
}

/// <param name="Caption">The post's own sentence, which is not the title again: on the reference
/// channel the title is the hook and the caption names who is telling it and what the payoff was.</param>
/// <param name="Teller">Who tells the story, by name: the camera half holds on their seat.</param>
public sealed record CondensedStory(string Title, string Caption, string Rationale, double Score,
    IReadOnlyList<(double Start, double End)> Pieces, IReadOnlyList<string> Emphasis, string Teller = "")
{
    public double Seconds => Pieces.Sum(x => x.End - x.Start);
}

public sealed record BrollMoment(double StartSeconds, double EndSeconds, string Query,
    string Subject = "", string VisualIntent = "", string SpokenText = "");

/// <param name="PrimarySubject">Who the short is about. Their footage backs every moment whose own
/// search comes up short, the way the reference channel cuts back to the guest's fights.</param>
/// <param name="PrimaryQuery">The search for that footage: the filmed event itself when there is one
/// ("Michael Jordan Lawrence Taylor golf"), otherwise the person's highlights.</param>
public sealed record BrollPlan(IReadOnlyList<BrollMoment> Moments, IReadOnlyList<string> Emphasis,
    string PrimarySubject = "", string PrimaryQuery = "");

public static class StorySchemas
{
    public const string Stories = """
    {"title":"WhopClipperStoriesV1","type":"object","additionalProperties":false,"required":["stories"],"properties":{"stories":{"type":"array","minItems":0,"maxItems":15,"items":{"type":"object","additionalProperties":false,"required":["title","caption","teller","rationale","score","pieces","emphasis"],"properties":{"title":{"type":"string"},"teller":{"type":"string"},"caption":{"type":"string"},"rationale":{"type":"string"},"score":{"type":"number","minimum":0,"maximum":1},"pieces":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"object","additionalProperties":false,"required":["startSeconds","endSeconds","firstWords","lastWords"],"properties":{"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},"firstWords":{"type":"string","minLength":1},"lastWords":{"type":"string","minLength":1}}}},"emphasis":{"type":"array","maxItems":20,"items":{"type":"string"}}}}}}}
    """;

    public const string Broll = """
    {"title":"WhopClipperBrollPlanV3","type":"object","additionalProperties":false,"required":["primarySubject","primaryQuery","moments","emphasis"],"properties":{"primarySubject":{"type":"string","minLength":2},"primaryQuery":{"type":"string","minLength":2},"moments":{"type":"array","minItems":1,"maxItems":30,"items":{"type":"object","additionalProperties":false,"required":["startSeconds","endSeconds","query","subject","visualIntent","spokenText"],"properties":{"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},"query":{"type":"string"},"subject":{"type":"string","minLength":2},"visualIntent":{"type":"string","minLength":8},"spokenText":{"type":"string","minLength":2}}}},"emphasis":{"type":"array","maxItems":20,"items":{"type":"string"}}}}
    """;
}

/// <summary>
/// Turns the AI's stories into cuts the renderer can trust. The limits are the ones measured on
/// Double Coverage TV: finished shorts run 14-40 seconds, built from pieces taken out of up to
/// eight minutes of the episode.
/// </summary>
public static class StoryParser
{
    public const double MinimumSeconds = 10;
    public const double MaximumSeconds = 60;
    public const double MaximumSpanSeconds = 600;

    /// <summary>
    /// The stretch of the episode a clip was cut from, first piece to last. A plan stores its start and its
    /// length, and for a condensed clip start + length stops long before its last piece. That made a 26 s cut
    /// of an 81 s story look like it ended 55 s early, so the same story was picked twice on 29 Sept.
    /// </summary>
    public static (double Start, double End) EpisodeSpan(EditPlan plan)
    {
        var split = JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.PodcastSplit;
        return split is { Pieces.Count: > 0 }
            ? (split.Pieces.Min(x => x.StartSeconds) + split.EpisodeOffsetSeconds,
               split.Pieces.Max(x => x.EndSeconds) + split.EpisodeOffsetSeconds)
            : (plan.StartSeconds, plan.EndSeconds);
    }

    /// <summary>True when a story shares more than 30% of its own length with a moment already clipped.</summary>
    public static bool OverlapsClipped(IReadOnlyList<(double Start, double End)> pieces,
        IEnumerable<(double Start, double End)> clipped)
    {
        if (pieces.Count == 0) return false;
        var start = pieces.Min(x => x.Start);
        var end = pieces.Max(x => x.End);
        var length = Math.Max(0.001, end - start);
        return clipped.Any(x => Math.Max(0, Math.Min(end, x.End) - Math.Max(start, x.Start)) > length * 0.3);
    }

    /// <param name="words">The episode transcript. When given, each piece is moved onto the words it quotes
    /// and a dangling lead-in before a splice is dropped (<see cref="SpliceSnapper"/>).</param>
    public static IReadOnlyList<CondensedStory> ParseStories(JsonElement root, double? episodeSeconds,
        IReadOnlyList<TranscriptWord>? words = null, double minimumSeconds = MinimumSeconds)
    {
        var result = new List<CondensedStory>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("stories", out var stories) ||
            stories.ValueKind != JsonValueKind.Array) return result;
        var ordered = words?.OrderBy(x => x.Start).ToList();

        foreach (var item in stories.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || ReadString(item, "title") is not { Length: > 0 } title) continue;
            var pieces = new List<(double Start, double End)>();
            if (item.TryGetProperty("pieces", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var piece in list.EnumerateArray())
                    if (TryNumber(piece, "startSeconds", out var start) && TryNumber(piece, "endSeconds", out var end) &&
                        start >= 0 && end > start && (episodeSeconds is null || end <= episodeSeconds.Value + 1))
                        pieces.Add(ordered is null ? (start, end)
                            : SpliceSnapper.SnapToQuotes((start, end), ReadString(piece, "firstWords"), ReadString(piece, "lastWords"), ordered));

            var merged = ordered is null ? Merge(pieces) : SpliceSnapper.TrimDanglingEnds(Merge(pieces), ordered);
            if (merged.Count == 0) continue;
            var seconds = merged.Sum(x => x.End - x.Start);
            if (seconds < minimumSeconds || seconds > MaximumSeconds) continue;
            if (merged[^1].End - merged[0].Start > MaximumSpanSeconds) continue;

            var score = TryNumber(item, "score", out var value) ? Math.Clamp(value, 0, 1) : 0;
            // The caption keeps its own punctuation and emoji: CleanHookText is for text burned on the
            // video, where a stray quote or dash matters, not for a line pasted into an upload form.
            result.Add(new CondensedStory(AiOutputParser.CleanHookText(title),
                (ReadString(item, "caption") ?? string.Empty).Trim(), ReadString(item, "rationale") ?? string.Empty,
                score, merged, ReadStrings(item, "emphasis"), (ReadString(item, "teller") ?? string.Empty).Trim()));
        }
        return result.OrderByDescending(x => x.Score).ToList();
    }

    /// <summary>How far apart two moments may start and end and still count as touching.</summary>
    public const double JoinToleranceSeconds = 0.75;

    public static BrollPlan? ParseBrollPlan(JsonElement root, double clipSeconds)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("moments", out var moments) ||
            moments.ValueKind != JsonValueKind.Array) return null;
        var parsed = new List<BrollMoment>();
        foreach (var item in moments.EnumerateArray())
        {
            if (!TryNumber(item, "startSeconds", out var start) || !TryNumber(item, "endSeconds", out var end) ||
                start < 0 || end <= start || start >= clipSeconds) continue;
            var query = ReadString(item, "query")?.Trim();
            if (query is null || query.Length is < 2 or > 120) continue;
            parsed.Add(new BrollMoment(start, Math.Min(end, clipSeconds), query,
                ReadString(item, "subject")?.Trim() ?? "", ReadString(item, "visualIntent")?.Trim() ?? "",
                ReadString(item, "spokenText")?.Trim() ?? ""));
        }
        if (parsed.Count == 0) return null;
        var ordered = parsed.OrderBy(x => x.StartSeconds).ToList();
        var contiguous = new List<BrollMoment>();
        var coveredUntil = 0.0;
        foreach (var moment in ordered)
        {
            // Missing semantic coverage must not silently inherit the last subject's footage. A join a
            // fraction of a second off is only rounding, though: a plan starting at the first word, 0.1s
            // in, was thrown out whole and cost the clip.
            if (Math.Abs(moment.StartSeconds - coveredUntil) > JoinToleranceSeconds) return null;
            contiguous.Add(moment with { StartSeconds = coveredUntil });
            coveredUntil = Math.Max(coveredUntil, moment.EndSeconds);
        }
        if (coveredUntil < clipSeconds - JoinToleranceSeconds) return null;
        // Reconcile sub-frame JSON rounding only. Larger semantic gaps and overlaps were rejected.
        contiguous[^1] = contiguous[^1] with { EndSeconds = clipSeconds };
        // A plan without a named primary falls back on the subject it spends longest on.
        var longest = contiguous.GroupBy(x => BrollPools.Key(x.Query))
            .OrderByDescending(g => g.Sum(x => x.EndSeconds - x.StartSeconds)).First().First();
        var primaryQuery = ReadString(root, "primaryQuery")?.Trim();
        if (primaryQuery is not { Length: >= 2 and <= 120 }) primaryQuery = longest.Query;
        var primarySubject = ReadString(root, "primarySubject")?.Trim();
        if (string.IsNullOrWhiteSpace(primarySubject)) primarySubject = longest.Subject;
        return new BrollPlan(contiguous, ReadStrings(root, "emphasis"), primarySubject, primaryQuery);
    }

    /// <summary>Overlapping pieces become one: the same words twice reads as a stutter.</summary>
    public static IReadOnlyList<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> pieces)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var piece in pieces.Where(x => x.End > x.Start).OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && piece.Start <= merged[^1].End + 0.05)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, piece.End));
            else
                merged.Add(piece);
        }
        return merged;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Take(20).ToList()
            : [];

    private static bool TryNumber(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value) && double.IsFinite(value);
    }
}

/// <summary>
/// Which seat on an episode's couch shot belongs to the person telling a story. Names are matched on a
/// shared word, so a teller written "Jared" or "Big Mike" finds "Jingle Jared" or "Mike Majlak".
/// </summary>
public static class CameraSeats
{
    public static (int Seat, int Seats)? Find(IReadOnlyList<string>? names, string? teller)
    {
        if (names is not { Count: >= 2 } || Words(teller) is not { Count: > 0 } wanted) return null;
        var scores = names.Select(name => Words(name).Count(wanted.Contains)).ToList();
        var best = scores.Max();
        // No shared word, or two seats sharing the same best match: no guess.
        if (best == 0 || scores.Count(x => x == best) > 1) return null;
        return (scores.IndexOf(best), names.Count);
    }

    private static HashSet<string> Words(string? text) => string.IsNullOrWhiteSpace(text) ? []
        : text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray()))
            // "Big" (Big Mike, Big Cat) is a nickname prefix, not a name, so it must not match two people.
            .Where(x => x.Length >= 3 && x is not ("big" or "the" or "and")).ToHashSet();
}

/// <summary>
/// Places a condensed story's cuts on real word boundaries. The AI reads a timestamp only at the start of
/// each transcript line, so its own times can fall mid-sentence; the first and last words it quotes cannot.
/// It then drops a dangling lead-in ("in hindsight", "and", "you know") from the end of a piece that jumps
/// to another moment: an unfinished phrase just before a cut was the splice Matthew rejected.
/// </summary>
public static class SpliceSnapper
{
    /// <summary>How far from the AI's own time a quoted word may be found.</summary>
    public const double SearchSeconds = 8;

    /// <summary>Two pieces closer than this play as one take, so the end of the first is not a splice.</summary>
    public const double SpliceGapSeconds = 0.4;

    // Longest first. Words that end a lead-in, never a sentence; "it", "you" or "so" can end a real
    // sentence ("I love it", "I think so") and are deliberately not here.
    private static readonly string[][] Dangling = new[]
    {
        "at the end of the day", "to be honest", "the thing is", "in hindsight", "and then", "but then", "so then",
        "and so", "you know", "i mean", "and", "but", "or", "because", "cause", "the", "a", "an", "um", "uh", "like",
    }.Select(x => x.Split(' ')).OrderByDescending(x => x.Length).ToArray();

    public static (double Start, double End) SnapToQuotes((double Start, double End) piece, string? firstWords,
        string? lastWords, IReadOnlyList<TranscriptWord> ordered)
    {
        var start = piece.Start;
        var end = piece.End;
        if (Tokens(firstWords) is { Length: > 0 } first && Find(ordered, first, piece.Start, fromEnd: false) is { } a)
            start = ordered[a].Start;
        if (Tokens(lastWords) is { Length: > 0 } last && Find(ordered, last, piece.End, fromEnd: true) is { } b)
            end = ordered[b].End;
        return end - start >= 0.5 ? (start, end) : piece;
    }

    public static IReadOnlyList<(double Start, double End)> TrimDanglingEnds(IReadOnlyList<(double Start, double End)> pieces,
        IReadOnlyList<TranscriptWord> ordered)
    {
        var result = pieces.ToList();
        for (var index = 0; index < result.Count - 1; index++)
        {
            if (result[index + 1].Start - result[index].End < SpliceGapSeconds) continue;
            var (start, end) = result[index];
            var inside = ordered.Where(x => x.Start >= start - 0.05 && x.Start < end).ToList();
            var trimmed = false;
            while (inside.Count > 3 && Dangling.FirstOrDefault(phrase => EndsWith(inside, phrase)) is { } phrase)
            {
                inside.RemoveRange(inside.Count - phrase.Length, phrase.Length);
                trimmed = true;
            }
            if (trimmed) result[index] = (start, Math.Min(end, inside[^1].End));
        }
        return result;
    }

    /// <summary>The word index where the quote starts (or ends, <paramref name="fromEnd"/>) nearest the AI's
    /// time, when at least 60% of its words match in order; null leaves the AI's own time.</summary>
    private static int? Find(IReadOnlyList<TranscriptWord> ordered, string[] quote, double near, bool fromEnd)
    {
        quote = fromEnd ? quote[^Math.Min(6, quote.Length)..] : quote[..Math.Min(6, quote.Length)];
        var needed = Math.Max(Math.Min(2, quote.Length), (int)Math.Ceiling(quote.Length * 0.6));
        int? best = null;
        var bestScore = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (Math.Abs(ordered[i].Start - near) > SearchSeconds) continue;
            var anchor = fromEnd ? i - quote.Length + 1 : i;
            if (anchor < 0 || anchor + quote.Length > ordered.Count) continue;
            var score = 0;
            for (var j = 0; j < quote.Length; j++)
                if (Normalize(ordered[anchor + j].Word) == quote[j]) score++;
            var distance = Math.Abs(ordered[i].Start - near);
            if (score >= needed && (score > bestScore || (score == bestScore && distance < bestDistance)))
                (best, bestScore, bestDistance) = (i, score, distance);
        }
        return best;
    }

    private static bool EndsWith(List<TranscriptWord> words, string[] phrase)
    {
        if (words.Count < phrase.Length) return false;
        for (var j = 0; j < phrase.Length; j++)
            if (Normalize(words[words.Count - phrase.Length + j].Word) != phrase[j]) return false;
        return true;
    }

    private static string[] Tokens(string? text) => string.IsNullOrWhiteSpace(text) ? []
        : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(x => x.Length > 0).ToArray();

    private static string Normalize(string word) =>
        new string(word.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
}

public sealed record BrollSlot(double AtSeconds, double DurationSeconds, int MomentIndex, string Query);

/// <param name="Source">Which downloaded file the stretch is in; stretches of one file with touching
/// times play as one continuous take.</param>
public sealed record BrollCleanStretch(int Index, double InSeconds, double DurationSeconds, int Source = 0);
public sealed record BrollCleanTake(int StretchIndex, double AtSeconds, double InSeconds, double DurationSeconds, string Query);
public sealed record BrollCleanAllocation(IReadOnlyList<BrollCleanTake> Takes, IReadOnlyList<BrollSlot> Gaps);

/// <summary>Consumes only measured clean intervals. A slot may use several intervals; it must never
/// read through the excluded frames between them or borrow footage from a different subject.</summary>
public static class BrollCleanAllocator
{
    private const double MinimumShotSeconds = 0.06;

    /// <summary>Small shortages may repeat already reviewed material from THIS moment. Every
    /// repeated take seeks back inside its measured interval; it never extends into unseen frames.</summary>
    public static IReadOnlyList<BrollCleanStretch> RepeatWithinMoment(IReadOnlyList<BrollCleanStretch> stretches,
        double secondsNeeded)
    {
        var available = stretches.Sum(x => x.DurationSeconds);
        var missing = secondsNeeded - available;
        if (missing <= 0.000001 || missing > secondsNeeded * 0.35 || available <= 0) return stretches;
        var result = stretches.ToList();
        foreach (var stretch in stretches)
        {
            if (missing <= 0.000001) break;
            var seconds = Math.Min(missing, stretch.DurationSeconds);
            if (seconds < MinimumShotSeconds) continue;
            result.Add(stretch with { DurationSeconds = seconds });
            missing -= seconds;
        }
        return result;
    }

    public static BrollCleanAllocation Allocate(IReadOnlyList<BrollSlot> slots, IReadOnlyList<BrollCleanStretch> stretches)
    {
        var queue = new BrollFootageQueue(stretches);
        var takes = new List<BrollCleanTake>();
        var gaps = new List<BrollSlot>();
        foreach (var slot in slots)
            if (queue.Fill(slot, takes) is { } gap) gaps.Add(gap);
        return new BrollCleanAllocation(takes, gaps);
    }

    /// <summary>
    /// Required footage cannot be borrowed from another moment. Clean reviewed material may be
    /// repeated within its own moment, but an uncovered subject needs replacement footage.
    /// </summary>
    public const double MaximumHeldFraction = 0;

    public static void RequireComplete(int shotCount, double gapSeconds, double clipSeconds)
    {
        if (shotCount == 0)
            throw new InvalidOperationException("No B-roll was found for this clip at all. It was not rendered, "
                + "because the bottom half would only be a blurred copy of the camera.");
        var allowed = Math.Max(0, clipSeconds) * MaximumHeldFraction;
        if (gapSeconds > allowed + 0.000001)
            throw new InvalidOperationException($"Required B-roll is incomplete ({gapSeconds:0.###} seconds uncovered, "
                + "with no reviewed footage for those moments). The clip was not rendered; "
                + "provide relevant footage or revise the moment's visual intent and queue the story again.");
    }
}

/// <summary>
/// One subject's reviewed footage, consumed front to back across every moment that shows it. Two
/// mentions of the same person get consecutive shots from the same source - the reference channel's
/// run of shots from one video - and never the same frames twice.
/// </summary>
/// <param name="cutAfterSeconds">When set, a take that has already run this long without a cut in its
/// source jumps forward at the next slot. The reference changes picture about every 2.2 seconds; one
/// continuous walkout take otherwise filled a whole 27-second Gaethje clip without a single cut.</param>
public sealed class BrollFootageQueue(IReadOnlyList<BrollCleanStretch> stretches, double? cutAfterSeconds = null)
{
    private const double MinimumShotSeconds = 0.06;
    private const double JumpSeconds = 0.8;
    private const double TouchingSeconds = 0.1;
    private int _cursor;
    private double _consumed;
    private int _lastSource = -1;
    private double _lastEnd;
    private double _run;

    public double RemainingSeconds =>
        Math.Max(0, stretches.Skip(_cursor).Sum(x => x.DurationSeconds) - _consumed);

    /// <returns>The end of the slot this footage could not fill, or null when it filled all of it.</returns>
    public BrollSlot? Fill(BrollSlot slot, ICollection<BrollCleanTake> takes, string? query = null)
    {
        if (cutAfterSeconds is { } limit && _run >= limit) Jump();
        var needed = slot.DurationSeconds;
        var at = slot.AtSeconds;
        while (needed > 0.000001)
        {
            if (_cursor >= stretches.Count) return slot with { AtSeconds = at, DurationSeconds = needed };
            var stretch = stretches[_cursor];
            var duration = Math.Min(stretch.DurationSeconds - _consumed, needed);
            // Avoid a sub-frame tail requiring an invalid FFmpeg shot. Leave it for the next
            // clean interval together with enough neighbouring time to make a real shot.
            var tail = needed - duration;
            if (tail > 0.000001 && tail < MinimumShotSeconds)
                duration -= MinimumShotSeconds - tail;
            if (duration < MinimumShotSeconds - 0.000001)
            {
                _cursor++;
                _consumed = 0;
                continue;
            }
            var inSeconds = stretch.InSeconds + _consumed;
            takes.Add(new BrollCleanTake(stretch.Index, at, inSeconds, duration, query ?? slot.Query));
            _run = stretch.Source == _lastSource && Math.Abs(inSeconds - _lastEnd) < TouchingSeconds ? _run + duration : duration;
            _lastSource = stretch.Source;
            _lastEnd = inSeconds + duration;
            _consumed += duration;
            at += duration;
            needed -= duration;
            if (stretch.DurationSeconds - _consumed < MinimumShotSeconds - 0.000001)
            {
                _cursor++;
                _consumed = 0;
            }
        }
        return null;
    }

    /// <summary>Skips a little of a continuous take so the next shot visibly changes. A real cut in the
    /// source already changes the picture, so the skip stops there instead of eating into the next shot.</summary>
    private void Jump()
    {
        if (_cursor >= stretches.Count) return;
        var here = stretches[_cursor];
        if (here.Source != _lastSource || Math.Abs(here.InSeconds + _consumed - _lastEnd) >= TouchingSeconds) return;
        var left = JumpSeconds;
        while (left > 0 && _cursor < stretches.Count)
        {
            var stretch = stretches[_cursor];
            var remaining = stretch.DurationSeconds - _consumed;
            if (remaining - left >= MinimumShotSeconds)
            {
                _consumed += left;
                return;
            }
            left -= remaining;
            _cursor++;
            _consumed = 0;
            if (_cursor >= stretches.Count) return;
            var next = stretches[_cursor];
            if (next.Source != stretch.Source ||
                Math.Abs(next.InSeconds - (stretch.InSeconds + stretch.DurationSeconds)) >= TouchingSeconds) return;
        }
    }
}

/// <param name="Key">The normalised query: moments that search the same words share one pool.</param>
/// <param name="Primary">The story's main subject, whose footage backs any moment that runs short.</param>
public sealed record BrollPool(string Key, string Query, string Subject, string VisualIntent, string SpokenText,
    double SecondsNeeded, bool Primary);

/// <summary>
/// One search per subject, not one per phrase. Measured on @DoubleCoverageTV, a short draws on two to
/// four sources - the guest's fights, the named rival, Dana White's press conference, the actual golf
/// outing - and runs several consecutive shots from each. Searching every two-second phrase on its own
/// asked for a dozen narrow videos, found almost none, and downloaded twelve seconds of each.
/// </summary>
public static class BrollPools
{
    public static string Key(string query) =>
        string.Join(' ', Regex.Split(query.ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0));

    /// <summary>Named subjects first, so a video about a specific fight is claimed by that fight; the
    /// primary pool searches last.</summary>
    public static IReadOnlyList<BrollPool> Build(BrollPlan plan)
    {
        var primaryKey = Key(plan.PrimaryQuery);
        var pools = plan.Moments.GroupBy(x => Key(x.Query)).Where(x => x.Key.Length > 0).Select(group =>
        {
            var first = group.OrderByDescending(x => x.EndSeconds - x.StartSeconds).First();
            var quotes = string.Join(" / ", group.Select(x => x.SpokenText.Trim()).Where(x => x.Length > 0).Distinct());
            return new BrollPool(group.Key, first.Query, first.Subject, first.VisualIntent, quotes,
                group.Sum(x => x.EndSeconds - x.StartSeconds), group.Key == primaryKey);
        }).ToList();
        if (primaryKey.Length > 0 && !pools.Any(x => x.Primary))
            pools.Add(new BrollPool(primaryKey, plan.PrimaryQuery.Trim(), plan.PrimarySubject,
                $"Real footage of {plan.PrimarySubject}", "", 0, true));
        return pools.OrderBy(x => x.Primary).ThenByDescending(x => x.SecondsNeeded).ToList();
    }

    /// <summary>Seconds of reviewed footage worth collecting. The primary pool is sized for the whole
    /// clip because it stands behind every other subject; a margin covers shots the review turns down.</summary>
    /// <remarks>The margin also pays for the jumps that give continuous takes the reference's cut rhythm.</remarks>
    public static double Target(BrollPool pool, double clipSeconds) =>
        (pool.Primary ? Math.Max(pool.SecondsNeeded, clipSeconds) : pool.SecondsNeeded) * 1.5 + 2;
}

/// <summary>
/// The plan is written from transcript lines stamped every few words, so its boundaries land up to a
/// second off. The reference channel switches footage on the word that names the new subject, so each
/// boundary moves onto the first words of that moment's own quote when they were said nearby.
/// </summary>
public static class BrollTiming
{
    private const double SearchSeconds = 1.5;
    private const double MinimumMomentSeconds = 0.3;

    public static IReadOnlyList<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(TranscriptAligner.Normalise).Where(x => x.Length > 0).ToList();

    public static BrollPlan SnapToQuotes(BrollPlan plan, IReadOnlyList<TranscriptWord> words)
    {
        var said = words.OrderBy(x => x.Start).Select(x => (x.Start, Key: TranscriptAligner.Normalise(x.Word)))
            .Where(x => x.Key.Length > 0).ToList();
        var moments = plan.Moments.ToList();
        for (var index = 1; index < moments.Count; index++)
        {
            var opening = Words(moments[index].SpokenText).Take(2).ToList();
            if (opening.Count == 0) continue;
            var planned = moments[index].StartSeconds;
            double? best = null;
            for (var at = 0; at + opening.Count <= said.Count; at++)
            {
                if (Math.Abs(said[at].Start - planned) > SearchSeconds) continue;
                if (!opening.Select((word, offset) => said[at + offset].Key == word).All(x => x)) continue;
                if (best is null || Math.Abs(said[at].Start - planned) < Math.Abs(best.Value - planned))
                    best = said[at].Start;
            }
            if (best is not { } start) continue;
            var boundary = Math.Round(Math.Max(0, start - 0.05), 3);
            // Never collapse a neighbouring moment: each keeps at least a short shot of its own.
            if (boundary < moments[index - 1].StartSeconds + MinimumMomentSeconds ||
                boundary > moments[index].EndSeconds - MinimumMomentSeconds) continue;
            moments[index - 1] = moments[index - 1] with { EndSeconds = boundary };
            moments[index] = moments[index] with { StartSeconds = boundary };
        }
        return plan with { Moments = moments };
    }
}

/// <summary>
/// Splits the bottom half into shots. Each moment the AI picked covers the clip from where it is
/// spoken until the next moment starts, cut into shots of about the reference's 2.2 seconds, so
/// the footage is never empty and always changes on the words that name it.
/// </summary>
public static class BrollTimeline
{
    public static IReadOnlyList<BrollSlot> Plan(IReadOnlyList<BrollMoment> moments, double clipSeconds, double shotSeconds = 2.2)
    {
        if (clipSeconds <= 0 || shotSeconds <= 0) return [];
        var kept = moments.Where(x => x.StartSeconds >= 0 && x.StartSeconds < clipSeconds && x.EndSeconds > x.StartSeconds)
            .OrderBy(x => x.StartSeconds).ToList();
        if (kept.Count == 0) return [];

        var slots = new List<BrollSlot>();
        for (var index = 0; index < kept.Count; index++)
        {
            var from = kept[index].StartSeconds;
            var to = Math.Min(kept[index].EndSeconds, clipSeconds);
            if (index + 1 < kept.Count) to = Math.Min(to, kept[index + 1].StartSeconds);
            var length = to - from;
            if (length <= 0) continue;
            var count = Math.Max(1, (int)Math.Round(length / shotSeconds, MidpointRounding.AwayFromZero));
            var each = length / count;
            for (var shot = 0; shot < count; shot++)
                slots.Add(new BrollSlot(from + shot * each, each, index, kept[index].Query));
        }
        return slots;
    }
}

public sealed record BrollSearchEntry(string Id, string Title, double? DurationSeconds, string? Channel,
    string? LiveStatus, int Order)
{
    public string Url => $"https://www.youtube.com/watch?v={Id}";
}

/// <summary>Chooses which search results are worth downloading a probe of.</summary>
public static class BrollCandidates
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "his", "her", "was", "are", "you", "what",
        "when", "how", "who", "vs", "video", "footage", "clip",
    };

    // These describe the kind of footage, not its subject. Two matches such as "knockout highlights"
    // must not make Mike Tyson footage pass a search for Teofimo Lopez.
    private static readonly HashSet<string> FootageWords = new(StringComparer.Ordinal)
    {
        "highlight", "highlights", "knockout", "knockouts", "training", "workout", "workouts",
        "fight", "fights", "fighting", "boxing", "interview", "interviews", "press", "conference",
        "game", "games", "winner", "winning", "compilation", "best", "moments", "career", "full",
        "official", "news", "event", "performance", "practice", "behind", "scenes", "arrives", "arrival",
        // Venue descriptors improve discovery but are not identities. A real UFC walkout video
        // can show the arena without putting "arena" in its title; frames decide that question.
        "arena", "stadium", "venue",
        "walkout", "walkouts", "entrance", "entrances", "intros", "walking", "walk",
    };

    public static IReadOnlyList<BrollSearchEntry> ParseSearch(string output)
    {
        var entries = new List<BrollSearchEntry>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) continue;
                double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) ? seconds : null;
                entries.Add(new BrollSearchEntry(id.GetString()!, Text(root, "title") ?? string.Empty, duration,
                    Text(root, "channel") ?? Text(root, "uploader"), Text(root, "live_status"), entries.Count));
            }
            catch (JsonException) { }
        }
        return entries;
    }

    /// <summary>
    /// Discovery filter only, never visual verification. A result must carry the query's names - videos
    /// are titled after people and events - but not its descriptive words: requiring every word of
    /// "Justin Gaethje parents mother father UFC fight night behind the scenes" matched no title on
    /// YouTube, and five of six Gaethje stories got no footage at all. Descriptive words only rank.
    /// The downloaded intervals then require a separate review of their actual frames.
    /// </summary>
    public static IReadOnlyList<BrollSearchEntry> Rank(IReadOnlyList<BrollSearchEntry> entries, string query, int take = 3)
    {
        var wanted = Tokens(query);
        wanted.Remove("roll");
        var required = RequiredWords(query);
        if (required.Count == 0) return [];
        var sports = SportsOf(wanted);
        return entries.Where(Usable)
            .Select(x => (Entry: x, TitleWords: Tokens(x.Title)))
            .Where(x => required.All(x.TitleWords.Contains))
            // Boxing footage is not an MMA fighter, and a UFC highlight is not his golf day.
            .Where(x => sports.Count == 0 || SportsOf(x.TitleWords) is not { Count: > 0 } found || found.Overlaps(sports))
            .Select(x => (x.Entry, Score: x.TitleWords.Count(wanted.Contains) + Bonus(x.Entry)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Order)
            .Take(take).Select(x => x.Entry).ToList();
    }

    /// <summary>
    /// The words a title must contain. Each capitalised run in the query starts with a name, and a title
    /// needs that name's surname: titles drop first names ("Gaethje vs Poirier 2"). Capitalised words after
    /// it only rank, because they are usually context the uploader did not repeat - "Justin Gaethje White
    /// House fight" must still find "Justin Gaethje Vs Ilia Topuria FULL FIGHT". Names split by "vs" or
    /// other words are separate runs and are all required. All-caps words (UFC, NFL) are context. A query
    /// with no names, or a generic "... b roll" search for an object, needs every non-footage word.
    /// </summary>
    public static IReadOnlyList<string> RequiredWords(string query)
    {
        var runs = IsGeneric(query) ? [] : NameRuns(query);
        if (runs.Count == 0)
            return Tokens(query).Where(x => x != "roll" && !FootageWords.Contains(x)).ToList();
        return runs.Select(run => run.Count >= 2 ? run[1] : run[0]).Distinct().ToList();
    }

    /// <summary>
    /// The names in a query alone, or null when there is nothing to strip. A second search on just the
    /// names finds the person's footage when the words around them narrowed the first one to nothing.
    /// </summary>
    public static string? Simplify(string query)
    {
        if (IsGeneric(query)) return null;
        var names = new List<string>();
        var run = new List<string>();
        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsNameWord(word)) { run.Add(word); continue; }
            if (run.Count > 0) names.Add(string.Join(' ', run));
            run.Clear();
        }
        if (run.Count > 0) names.Add(string.Join(' ', run));
        if (names.Count == 0) return null;
        var simplified = string.Join(' ', names);
        return simplified.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) ? null : simplified;
    }

    private static bool IsGeneric(string query) =>
        query.Contains("b roll", StringComparison.OrdinalIgnoreCase) || query.Contains("b-roll", StringComparison.OrdinalIgnoreCase)
        || query.Contains("broll", StringComparison.OrdinalIgnoreCase);

    private static bool IsNameWord(string word)
    {
        var letters = word.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && char.IsUpper(letters[0]) && letters.Skip(1).Any(char.IsLower)
            && NameToken(word) is not null;
    }

    private static string? NameToken(string word) =>
        Tokens(word).Where(x => !FootageWords.Contains(x)).OrderByDescending(x => x.Length).FirstOrDefault();

    private static List<List<string>> NameRuns(string query)
    {
        var runs = new List<List<string>>();
        var run = new List<string>();
        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsNameWord(word)) { run.Add(NameToken(word)!); continue; }
            if (run.Count > 0) runs.Add(run);
            run = [];
        }
        if (run.Count > 0) runs.Add(run);
        return runs;
    }

    private static readonly string[][] Sports =
    [
        ["mma", "ufc", "bellator", "pfl", "wsof", "octagon"],
        ["boxing", "boxer"],
        ["nfl", "football"],
        ["nba", "basketball"],
        ["mlb", "baseball"],
        ["golf", "golfing", "pga"],
        ["wwe", "wrestlemania"],
        ["nhl", "hockey"],
    ];

    private static HashSet<int> SportsOf(IReadOnlySet<string> tokens) =>
        Enumerable.Range(0, Sports.Length).Where(i => Sports[i].Any(tokens.Contains)).ToHashSet();

    // The leagues' and promoters' own uploads are the reference channel's usual source.
    private static readonly Regex OfficialChannel = new(
        @"\b(ufc|espn|top rank|dazn|matchroom|premier boxing|golden boy|showtime|nfl|nba|mlb|pga tour|tnt sports|fox sports|sky sports|bellator|pfl|wwe)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FootageTitle = new(
        @"\b(highlights?|full fight|free fight|knockouts?|finishes|full game|walkouts?|official music video|vlog|behind the scenes|press conference)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Someone else talking about the subject: a studio, a podcast, an analyst. It is the subject's name
    // in the title over a stranger's face.
    private static readonly Regex TalkingHeads = new(
        @"\b(podcast|breakdown|analysis|analyst|predictions?|predicts|picks|reacts?|reaction|responds|reveals|explains|rant|debate|opens up|tier list)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Action first: an official channel's sit-down preview is still a sit-down.
    private static int Bonus(BrollSearchEntry entry) =>
        (entry.Channel is { } channel && OfficialChannel.IsMatch(channel) ? 1 : 0)
        + (FootageTitle.IsMatch(entry.Title) ? 2 : 0)
        - (TalkingHeads.IsMatch(entry.Title) ? 3 : 0);

    /// <summary>Starts past the intro, where highlight reels and interviews have a logo sting or a
    /// title card, and leaves room for a 20 second probe before the end.</summary>
    public static double ProbeStart(double durationSeconds) =>
        Math.Clamp(durationSeconds * 0.12, 4, Math.Max(4, durationSeconds - 25));

    /// <summary>Scripted and re-edited video. A drama scene or a sketch is not footage of anything that happened,
    /// and skit and reaction channels burn their own captions over the picture, which fights the clip's captions.</summary>
    // The campaign brief bans AI-generated video; fan "AI cinematic shorts" of real fights come up in
    // ordinary searches for a fighter's name.
    private static readonly Regex Scripted = new(
        @"\b(s\d{1,2}\s?ep?\d{1,2}|season\s+\d+|episode\s+\d+|full\s+episode|movie|movies|film|trailer|scene|sitcom|sketch|skit|parody|reaction|reacts|dhar\s+mann|snl|movieclips|official\s+clip|rocky|creed|southpaw|raging\s+bull|million\s+dollar\s+baby|cinderella\s+man|hands\s+of\s+stone|bleed\s+for\s+this|ai\s+(generated|cinematic|video|short|animation)|cinematic\s+short)\b|\((19|20)\d{2}\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Film studios and their clip channels: a Rocky scene came through under a title with no "movie" in it.</summary>
    private static readonly Regex FilmChannels = new(
        @"movieclips|netflix|hbo|warner|paramount|sony pictures|universal pictures|rotten tomatoes|filmselect|lionsgate|\bmgm\b|disney|prime video",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The app has its own music library. A walkout *song* upload is often a still image, not
    // evidence of an athlete walking out; do not spend the visual-review budget on it. An official
    // music video is real footage, and the reference used "Ric Flair Drip" for Ric Flair's Migos story.
    private static readonly Regex MusicOnlyTitle = new(
        @"\b(song|lyrics?|official audio|visuali[sz]er|walkout edition)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool Usable(BrollSearchEntry entry) =>
        entry.DurationSeconds is >= 45 and <= 4 * 3600
        && entry.LiveStatus is not ("is_live" or "is_upcoming" or "post_live")
        && !entry.Title.Contains("#shorts", StringComparison.OrdinalIgnoreCase)
        && !Scripted.IsMatch(entry.Title)
        && !MusicOnlyTitle.IsMatch(entry.Title)
        && !(entry.Channel is { } studio && FilmChannels.IsMatch(studio))
        && !(entry.Channel is { } musicChannel && musicChannel.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        // The channel's own shorts are the edits being copied, never source footage.
        && !(entry.Channel is { } channel && Regex.IsMatch(Regex.Replace(channel.ToLowerInvariant(), "[^a-z0-9]", string.Empty), "doub(le|l3)coverage"));

    private static HashSet<string> Tokens(string text)
    {
        var tokens = Regex.Split(Regex.Replace(text.ToLowerInvariant(), @"ufc(?=\d)", "ufc "), "[^a-z0-9]+")
            .Where(x => x.Length >= 3 && !StopWords.Contains(x)).ToHashSet(StringComparer.Ordinal);
        // UFC footage is MMA, but generic MMA is not necessarily UFC. Boxing is never an alias.
        if (tokens.Contains("ufc")) tokens.Add("mma");
        return tokens;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Pulls a schema payload out of whatever the CLI printed: Claude's result envelope with
/// structured_output, a bare JSON document, a fenced block, or Codex's JSONL event stream.
/// </summary>
public static class StructuredOutput
{
    public static JsonElement? Extract(string output, Func<JsonElement, bool> accept)
    {
        var direct = Unwrap(StripFence(output), accept);
        if (direct is not null) return direct;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var eventType) ||
                    eventType.GetString() != "item.completed" || !root.TryGetProperty("item", out var item) ||
                    item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var itemType) ||
                    itemType.GetString() != "agent_message" || !item.TryGetProperty("text", out var text) ||
                    text.ValueKind != JsonValueKind.String) continue;
                var parsed = Unwrap(StripFence(text.GetString() ?? string.Empty), accept);
                if (parsed is not null) return parsed;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static JsonElement? Unwrap(string payload, Func<JsonElement, bool> accept)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("structured_output", out var structured))
                return structured.ValueKind == JsonValueKind.String
                    ? Unwrap(StripFence(structured.GetString() ?? string.Empty), accept)
                    : accept(structured) ? structured.Clone() : null;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                return Unwrap(StripFence(result.GetString() ?? string.Empty), accept);
            return accept(root) ? root.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string StripFence(string value)
    {
        var candidate = value.Trim();
        if (!candidate.StartsWith("```", StringComparison.Ordinal)) return candidate;
        var firstLine = candidate.IndexOf('\n');
        var lastFence = candidate.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine ? candidate[(firstLine + 1)..lastFence].Trim() : candidate;
    }
}
