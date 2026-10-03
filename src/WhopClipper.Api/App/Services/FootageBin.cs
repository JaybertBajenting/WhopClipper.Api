using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

/// <summary>One video in a campaign's footage, downloaded from a link pasted in a clip's B-roll editor.</summary>
/// <param name="FromSeconds">Where the download starts in the linked video; null from the beginning.</param>
/// <param name="FileName">The downloaded file beside this record, once it has arrived.</param>
/// <param name="Channel">Who uploaded the linked video.</param>
/// <param name="SourceDurationSeconds">How long the whole linked video is, not just the part downloaded.</param>
/// <param name="AddedForPlanId">The clip whose editor the link was pasted into.</param>
public sealed record FootageItem(Guid Id, string Url, string Title, double? FromSeconds, double? ToSeconds, string Status,
    string? FileName, double DurationSeconds, int Width, int Height, string? Error, DateTimeOffset CreatedAt, Guid? JobId = null,
    string? Channel = null, double? SourceDurationSeconds = null, Guid? AddedForPlanId = null);

/// <summary>Where a download has got to. Held in memory only: it is gone the moment the download ends.</summary>
/// <param name="Fraction">0 to 1 of the stretch downloaded, or null before the download proper has started.</param>
public sealed record FootageProgress(string Stage, double? Fraction = null, long? Bytes = null);

/// <summary>
/// A campaign's footage: videos downloaded from links pasted into any of its clips' B-roll editors. It is shared by
/// every clip, so a video fetched for one clip is there to use again on another shot or another clip, and the same
/// stretch of a video is never downloaded twice. Each video sits beside a small JSON record of where it came from.
/// </summary>
public static class FootageBin
{
    public const string Downloading = "Downloading";
    public const string Ready = "Ready";
    public const string Failed = "Failed";

    /// <summary>The longest stretch one download keeps. B-roll uses seconds of a video, and a whole fight card
    /// would be gigabytes.</summary>
    public const double MaxSeconds = 30 * 60;

    private static readonly ConcurrentDictionary<Guid, FootageProgress> Progress = new();

    public static string Folder(WorkspaceService workspace, Guid campaignId) =>
        workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "intermediate", "footage"));

    private static string RecordPath(string folder, Guid id) => Path.Combine(folder, id.ToString("N") + ".json");

    public static IReadOnlyList<FootageItem> List(string folder)
    {
        AdoptPerClipBins(folder);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Select(Read).OfType<FootageItem>().OrderBy(x => x.CreatedAt).ToList()
            : [];
    }

    public static FootageItem? Find(string folder, Guid id)
    {
        AdoptPerClipBins(folder);
        return Read(RecordPath(folder, id));
    }

    /// <summary>Written to a temporary file and moved into place, so the editor, which reads the records every second
    /// while something downloads, never sees half a record.</summary>
    public static void Write(string folder, FootageItem item)
    {
        Directory.CreateDirectory(folder);
        var path = RecordPath(folder, item.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(item, JsonDefaults.Serializer), new UTF8Encoding(false));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(25); }
        }
    }

    /// <summary>The downloaded video, or null while it has not arrived. The name comes from the record, so it is
    /// reduced to a bare file name and can never point outside the bin.</summary>
    public static string? FilePath(string folder, FootageItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FileName)) return null;
        var path = Path.Combine(folder, Path.GetFileName(item.FileName));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Removes a video and its record from the bin.</summary>
    public static void Remove(string folder, FootageItem item, Action<string> discard)
    {
        if (FilePath(folder, item) is { } video) discard(video);
        var record = RecordPath(folder, item.Id);
        if (File.Exists(record)) discard(record);
    }

    public static void ReportProgress(Guid id, FootageProgress progress) => Progress[id] = progress;
    public static FootageProgress? ProgressOf(Guid id) => Progress.TryGetValue(id, out var progress) ? progress : null;
    public static void EndProgress(Guid id) => Progress.TryRemove(id, out _);

    /// <summary>What identifies the video behind a link: the YouTube id however the link is written (watch, youtu.be,
    /// shorts, with or without a timestamp), or the link itself for any other site.</summary>
    public static string VideoKey(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url.Trim();
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal))
        {
            var id = LocalSourceScanner.VideoIdFromUrl(uri.ToString());
            if (id.Length > 0) return "youtube:" + id;
        }
        return uri.GetLeftPart(UriPartial.Query);
    }

    /// <summary>The YouTube id behind a link, for its thumbnail; null for any other site.</summary>
    public static string? YouTubeId(string url) => VideoKey(url) is var key && key.StartsWith("youtube:", StringComparison.Ordinal)
        ? key["youtube:".Length..] : null;

    /// <summary>
    /// Footage that already holds the stretch asked for, and where in it that stretch starts. A download that is still
    /// running counts too, so pasting the same link twice does not start a second copy. Empty
    /// <paramref name="to"/> means the rest of the video, which only a download that also ran to the end can hold.
    /// </summary>
    public static (FootageItem Item, double InSeconds)? FindCovering(IEnumerable<FootageItem> items, string url,
        double? from, double? to)
    {
        var key = VideoKey(url);
        var start = Math.Max(0, from ?? 0);
        const double slack = 0.05;
        foreach (var item in items.Where(x => x.Status is Ready or Downloading && VideoKey(x.Url) == key)
                     .OrderByDescending(x => x.Status == Ready).ThenBy(x => x.CreatedAt))
        {
            var itemStart = item.FromSeconds ?? 0;
            if (itemStart > start + slack) continue;
            bool covers;
            if (to is not { } end) covers = item.ToSeconds is null;
            else
            {
                // A stretch asked for past the end of the video is held by anything that reaches the end.
                if (item.SourceDurationSeconds is { } length) end = Math.Min(end, length);
                var itemEnd = item.Status == Ready ? itemStart + item.DurationSeconds : item.ToSeconds ?? double.PositiveInfinity;
                covers = itemEnd >= end - slack;
            }
            if (covers) return (item, Math.Round(Math.Max(0, start - itemStart), 3));
        }
        return null;
    }

    /// <summary>
    /// The window of a picked video that fills the 1080x960 lower panel. A wide frame keeps its full height and
    /// <paramref name="framing"/> slides the window from the left edge (0) to the right (1); a tall frame keeps its
    /// full width and is centred.
    /// </summary>
    public static CropRectDto Crop(int width, int height, double framing)
    {
        const double aspect = 1080d / 960d;
        framing = double.IsFinite(framing) ? Math.Clamp(framing, 0, 1) : 0.5;
        if (width >= height * aspect)
        {
            var w = Even((int)Math.Round(height * aspect));
            return new CropRectDto((int)Math.Round((width - w) * framing), 0, w, Even(height));
        }
        var h = Even((int)Math.Round(width / aspect));
        return new CropRectDto(0, Even((height - h) / 2), Even(width), h);
    }

    private static int Even(int value) => Math.Max(2, value - value % 2);

    /// <summary>
    /// Footage used to be kept per clip, in a folder named after the clip. Those videos move up into the campaign's
    /// shared footage, remembering which clip they were added for. Names are ids, so nothing can collide.
    /// </summary>
    private static void AdoptPerClipBins(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var bin in Directory.EnumerateDirectories(folder))
        {
            if (!Guid.TryParseExact(Path.GetFileName(bin), "N", out var planId)) continue;
            foreach (var record in Directory.EnumerateFiles(bin, "*.json"))
            {
                if (Read(record) is not { } item) continue;
                if (FilePath(bin, item) is { } video)
                {
                    var target = Path.Combine(folder, Path.GetFileName(video));
                    if (!File.Exists(target)) File.Move(video, target);
                }
                Write(folder, item with { AddedForPlanId = item.AddedForPlanId ?? planId });
                File.Delete(record);
            }
            if (!Directory.EnumerateFileSystemEntries(bin).Any()) Directory.Delete(bin);
        }
    }

    private static FootageItem? Read(string path)
    {
        if (!File.Exists(path)) return null;
        for (var attempt = 0; ; attempt++)
        {
            try { return JsonSerializer.Deserialize<FootageItem>(File.ReadAllText(path), JsonDefaults.Serializer); }
            catch (JsonException) { return null; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
            catch (IOException) { return null; }
        }
    }
}
