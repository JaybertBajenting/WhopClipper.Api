using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// The day a source video was published, kept in the source's metadata as yt-dlp names it ("upload_date",
/// "yyyyMMdd"). It decides the order sources and clips are listed in, newest first, because a vlog from last week is
/// worth posting before one from last year. Kept in the metadata rather than a column of its own: older sources get
/// it filled in later, and a missing or odd value must never stop a list from showing.
/// </summary>
public static class SourceDates
{
    public const string UploadDateKey = "upload_date";
    /// <summary>Set once a lookup has been made, found or not, so a video with no date is not asked about again.</summary>
    public const string CheckedKey = "upload_date_checked";

    public static DateOnly? PublishedOn(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty(UploadDateKey, out var day) && day.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(day.GetString(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
            foreach (var key in new[] { "timestamp", "release_timestamp" })
                if (root.TryGetProperty(key, out var stamp) && stamp.ValueKind == JsonValueKind.Number &&
                    stamp.TryGetInt64(out var seconds) && seconds > 0)
                    return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Whether the date was looked up already, whatever the answer.</summary>
    public static bool WasChecked(string? metadataJson)
    {
        if (PublishedOn(metadataJson) is not null) return true;
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(CheckedKey, out var checkedFlag) && checkedFlag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// The metadata with yt-dlp's upload day added and marked as looked up. Everything already there is kept;
    /// metadata that is not a JSON object is started afresh. "NA" or anything unreadable records only the lookup.
    /// </summary>
    public static string WithUploadDate(string? metadataJson, string? uploadDate)
    {
        JsonObject metadata;
        try { metadata = JsonNode.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson) as JsonObject ?? []; }
        catch (JsonException) { metadata = []; }
        var value = uploadDate?.Trim();
        if (DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            metadata[UploadDateKey] = value;
        metadata[CheckedKey] = true;
        return metadata.ToJsonString();
    }

    /// <summary>Sources newest video first; those with no known date after them, the most recently added first.</summary>
    public static IEnumerable<SourceAsset> NewestFirst(IEnumerable<SourceAsset> sources) => sources
        .Select(x => (Source: x, Day: PublishedOn(x.MetadataJson)))
        .OrderByDescending(x => x.Day.HasValue).ThenByDescending(x => x.Day).ThenByDescending(x => x.Source.CreatedAt)
        .Select(x => x.Source);
}
