using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Reads the per-word "en-orig" json3 caption track YouTube generates for an episode. Each segment
/// carries its own offset, so a whole hour's words arrive timed without downloading or transcribing
/// the video - which matters when the drive cannot hold the episode at all.
/// </summary>
public static class YouTubeCaptions
{
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
}

public sealed record CondensedStory(string Title, string Rationale, double Score,
    IReadOnlyList<(double Start, double End)> Pieces, IReadOnlyList<string> Emphasis)
{
    public double Seconds => Pieces.Sum(x => x.End - x.Start);
}

public sealed record BrollMoment(double StartSeconds, double EndSeconds, string Query);
public sealed record BrollPlan(IReadOnlyList<BrollMoment> Moments, IReadOnlyList<string> Emphasis);

public static class StorySchemas
{
    public const string Stories = """
    {"title":"WhopClipperStoriesV1","type":"object","additionalProperties":false,"required":["stories"],"properties":{"stories":{"type":"array","minItems":1,"maxItems":10,"items":{"type":"object","additionalProperties":false,"required":["title","rationale","score","pieces","emphasis"],"properties":{"title":{"type":"string"},"rationale":{"type":"string"},"score":{"type":"number","minimum":0,"maximum":1},"pieces":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"object","additionalProperties":false,"required":["startSeconds","endSeconds"],"properties":{"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0}}}},"emphasis":{"type":"array","maxItems":20,"items":{"type":"string"}}}}}}}
    """;

    public const string Broll = """
    {"title":"WhopClipperBrollPlanV1","type":"object","additionalProperties":false,"required":["moments","emphasis"],"properties":{"moments":{"type":"array","minItems":1,"maxItems":30,"items":{"type":"object","additionalProperties":false,"required":["startSeconds","endSeconds","query"],"properties":{"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},"query":{"type":"string"}}}},"emphasis":{"type":"array","maxItems":20,"items":{"type":"string"}}}}
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

    public static IReadOnlyList<CondensedStory> ParseStories(JsonElement root, double? episodeSeconds)
    {
        var result = new List<CondensedStory>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("stories", out var stories) ||
            stories.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in stories.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || ReadString(item, "title") is not { Length: > 0 } title) continue;
            var pieces = new List<(double Start, double End)>();
            if (item.TryGetProperty("pieces", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var piece in list.EnumerateArray())
                    if (TryNumber(piece, "startSeconds", out var start) && TryNumber(piece, "endSeconds", out var end) &&
                        start >= 0 && end > start && (episodeSeconds is null || end <= episodeSeconds.Value + 1))
                        pieces.Add((start, end));

            var merged = Merge(pieces);
            if (merged.Count == 0) continue;
            var seconds = merged.Sum(x => x.End - x.Start);
            if (seconds is < MinimumSeconds or > MaximumSeconds) continue;
            if (merged[^1].End - merged[0].Start > MaximumSpanSeconds) continue;

            var score = TryNumber(item, "score", out var value) ? Math.Clamp(value, 0, 1) : 0;
            result.Add(new CondensedStory(AiOutputParser.CleanHookText(title), ReadString(item, "rationale") ?? string.Empty,
                score, merged, ReadStrings(item, "emphasis")));
        }
        return result.OrderByDescending(x => x.Score).ToList();
    }

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
            parsed.Add(new BrollMoment(start, Math.Min(end, clipSeconds), query));
        }
        return parsed.Count == 0 ? null : new BrollPlan(parsed.OrderBy(x => x.StartSeconds).ToList(), ReadStrings(root, "emphasis"));
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

public sealed record BrollSlot(double AtSeconds, double DurationSeconds, int MomentIndex, string Query);

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
        var kept = new List<BrollMoment>();
        foreach (var moment in moments.Where(x => x.StartSeconds < clipSeconds).OrderBy(x => x.StartSeconds))
            // A moment landing within 0.6s of the previous one would get a flash of a shot; the
            // earlier footage simply carries on.
            if (kept.Count == 0 || moment.StartSeconds >= kept[^1].StartSeconds + 0.6) kept.Add(moment);
        if (kept.Count == 0) return [];

        var slots = new List<BrollSlot>();
        for (var index = 0; index < kept.Count; index++)
        {
            var from = index == 0 ? 0 : kept[index].StartSeconds;
            var to = index + 1 < kept.Count ? kept[index + 1].StartSeconds : clipSeconds;
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
    /// A result has to share words with its search to be used at all. Taking the top results whatever they
    /// matched put a stranger boxing in a forest, and an ESPN studio desk, under Teofimo's warning to Ryan
    /// Garcia. A named search needs two of its words in the title; a plain "b roll" search needs one.
    /// </summary>
    public static IReadOnlyList<BrollSearchEntry> Rank(IReadOnlyList<BrollSearchEntry> entries, string query, int take = 3)
    {
        var wanted = Tokens(query);
        wanted.Remove("roll");
        var generic = query.Contains("b roll", StringComparison.OrdinalIgnoreCase) || query.Contains("b-roll", StringComparison.OrdinalIgnoreCase);
        var required = generic || wanted.Count < 2 ? 1 : 2;
        return entries.Where(Usable)
            .Select(x => (Entry: x, Overlap: Tokens(x.Title).Count(wanted.Contains)))
            .Where(x => x.Overlap >= required)
            .OrderByDescending(x => x.Overlap).ThenBy(x => x.Entry.Order)
            .Take(take).Select(x => x.Entry).ToList();
    }

    /// <summary>Starts past the intro, where highlight reels and interviews have a logo sting or a
    /// title card, and leaves room for a 20 second probe before the end.</summary>
    public static double ProbeStart(double durationSeconds) =>
        Math.Clamp(durationSeconds * 0.12, 4, Math.Max(4, durationSeconds - 25));

    /// <summary>Scripted and re-edited video. A drama scene or a sketch is not footage of anything that happened,
    /// and skit and reaction channels burn their own captions over the picture, which fights the clip's captions.</summary>
    private static readonly Regex Scripted = new(
        @"\b(s\d{1,2}\s?ep?\d{1,2}|season\s+\d+|episode\s+\d+|full\s+episode|movie|movies|film|trailer|scene|sitcom|sketch|skit|parody|reaction|reacts|dhar\s+mann|snl|movieclips|official\s+clip)\b|\((19|20)\d{2}\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Film studios and their clip channels: a Rocky scene came through under a title with no "movie" in it.</summary>
    private static readonly Regex FilmChannels = new(
        @"movieclips|netflix|hbo|warner|paramount|sony pictures|universal pictures|rotten tomatoes|filmselect|lionsgate|\bmgm\b|disney|prime video",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool Usable(BrollSearchEntry entry) =>
        entry.DurationSeconds is >= 45 and <= 4 * 3600
        && entry.LiveStatus is not ("is_live" or "is_upcoming" or "post_live")
        && !entry.Title.Contains("#shorts", StringComparison.OrdinalIgnoreCase)
        && !Scripted.IsMatch(entry.Title)
        && !(entry.Channel is { } studio && FilmChannels.IsMatch(studio))
        // The channel's own shorts are the edits being copied, never source footage.
        && !(entry.Channel is { } channel && Regex.IsMatch(Regex.Replace(channel.ToLowerInvariant(), "[^a-z0-9]", string.Empty), "doub(le|l3)coverage"));

    private static HashSet<string> Tokens(string text) =>
        Regex.Split(text.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(x => x.Length >= 3 && !StopWords.Contains(x))
            .ToHashSet(StringComparer.Ordinal);

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
