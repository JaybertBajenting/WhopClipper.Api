using System.Text.Json;

namespace WhopClipper.Api.App.Services;

/// <summary>Metadata only discovers candidates. These decisions refer to labelled frames from
/// the actual clean intervals, in the same crop that will appear below the podcast.</summary>
public sealed record BrollShotReview(int ShotIndex, bool Approved, string VisibleContent, string Reason);
public sealed record BrollStoryContext(string EpisodeTitle, string StoryTitle, string TimedTranscript);

public static class BrollVisualReview
{
    public const string Schema = """
        {"title":"WhopClipperBrollVisualReviewV1","type":"object","additionalProperties":false,"required":["framesInspected","shots"],"properties":{"framesInspected":{"type":"boolean"},"shots":{"type":"array","maxItems":15,"items":{"type":"object","additionalProperties":false,"required":["shotIndex","approved","visibleContent","reason"],"properties":{"shotIndex":{"type":"integer","minimum":0},"approved":{"type":"boolean"},"visibleContent":{"type":"string"},"reason":{"type":"string"}}}}}}
        """;

    public static IReadOnlyList<BrollShotReview> Parse(JsonElement result, int shotCount)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("framesInspected", out var inspected) || inspected.ValueKind != JsonValueKind.True ||
            !result.TryGetProperty("shots", out var shots) || shots.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The B-roll reviewer could not inspect the supplied frames. No candidate was accepted.");
        var decisions = new List<BrollShotReview>();
        var seen = new HashSet<int>();
        foreach (var item in shots.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("shotIndex", out var id) ||
                !id.TryGetInt32(out var index) || index < 0 || index >= shotCount || !seen.Add(index) ||
                !item.TryGetProperty("approved", out var approved) || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !item.TryGetProperty("visibleContent", out var content) || content.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()) || string.IsNullOrWhiteSpace(reason.GetString()))
                throw new InvalidOperationException("The B-roll frame review was incomplete or ambiguous. No candidate was accepted.");
            decisions.Add(new(index, approved.GetBoolean(), content.GetString()!, reason.GetString()!));
        }
        if (seen.Count != shotCount)
            throw new InvalidOperationException("The B-roll review omitted candidate shots. No candidate was accepted.");
        return decisions.OrderBy(x => x.ShotIndex).ToList();
    }

    /// <summary>
    /// Every moment must name what it shows, and most must quote words actually said inside it - that is
    /// what proves the timing. One paraphrased quote ("fights" for "fighting") does not make the plan
    /// wrong, so it no longer costs the whole clip.
    /// </summary>
    public static bool IsGrounded(BrollPlan plan, IReadOnlyList<TranscriptWord> words) =>
        plan.Moments.Count > 0
        && plan.Moments.All(x => !string.IsNullOrWhiteSpace(x.Subject) && x.VisualIntent.Trim().Length >= 8)
        && plan.Moments.Count(x => HasGroundedIntent(x, words)) >= Math.Ceiling(plan.Moments.Count * 0.6);

    /// <summary>
    /// Most of the quote must have been said around this moment. Not verbatim inside it: the plan is
    /// written from lines stamped every few words, so a correct Gaethje plan lost three of six moments
    /// to a boundary 0.3s early, a quote running one word long, and "interim" where whisper heard "intern".
    /// </summary>
    public static bool HasGroundedIntent(BrollMoment moment, IReadOnlyList<TranscriptWord> words)
    {
        if (string.IsNullOrWhiteSpace(moment.Subject) || moment.VisualIntent.Trim().Length < 8 ||
            string.IsNullOrWhiteSpace(moment.SpokenText)) return false;
        var quote = BrollTiming.Words(moment.SpokenText);
        if (quote.Count == 0) return false;
        var said = words.Where(x => x.End > moment.StartSeconds - 1.5 && x.Start < moment.EndSeconds + 1.5)
            .Select(x => TranscriptAligner.Normalise(x.Word)).Where(x => x.Length > 0).ToList();
        var found = quote.Count(said.Remove);
        return found >= Math.Ceiling(quote.Count * 0.7);
    }
}
