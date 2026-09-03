using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace WhopClipper.Api.App.Services;

public enum DriveTargetKind { File, Folder }

public sealed record DriveTarget(DriveTargetKind Kind, string Id);

/// <param name="SizeBytes">Exact size from <c>Content-Range</c>, so the disk guard can be told the
/// truth instead of a guess derived from a duration nobody knows yet.</param>
public sealed record DriveFile(string Id, string FileName, string ContentType, long SizeBytes)
{
    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Raised when Drive answers with a page instead of a file, so the cause can be named.</summary>
public sealed class DriveQuotaException(string message) : Exception(message);

/// <summary>
/// Fetches Google Drive media without yt-dlp, whose Drive extractor answers HTTP 400 on single
/// files and crashes outright on folders ("expected string or bytes-like object, got 'bool'").
///
/// Everything goes through one endpoint. `confirm=t` clears the virus-scan interstitial that Drive
/// puts in front of large files, and the `uuid` its form also carries turns out not to be required,
/// so no HTML has to be scraped to download anything.
/// </summary>
public sealed partial class GoogleDriveClient(IHttpClientFactory factory, ILogger<GoogleDriveClient> logger)
{
    public const string ClientName = "GoogleDrive";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WhopClipper/1.0";

    private static string DownloadUrl(string id) =>
        $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(id)}&export=download&confirm=t";

    public static bool IsDriveUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the id out of the shapes Drive hands people when they press Share.</summary>
    public static DriveTarget? TryParse(string? url)
    {
        if (!IsDriveUrl(url)) return null;
        var uri = new Uri(url!);
        if (FolderPathRegex().Match(uri.AbsolutePath) is { Success: true } folder)
            return new DriveTarget(DriveTargetKind.Folder, folder.Groups[1].Value);
        if (FilePathRegex().Match(uri.AbsolutePath) is { Success: true } file)
            return new DriveTarget(DriveTargetKind.File, file.Groups[1].Value);
        // /open?id=, /uc?id=
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query)["id"];
        return string.IsNullOrWhiteSpace(query) ? null : new DriveTarget(DriveTargetKind.File, query);
    }

    /// <summary>
    /// One byte, and everything worth knowing: type, real filename and exact size. Also the test for
    /// whether an id is a file at all - a folder id answers 416 with an HTML body, which is what lets
    /// <see cref="ListFolderAsync"/> validate its own scraping instead of trusting Drive's markup.
    /// </summary>
    public async Task<DriveFile?> ProbeAsync(string id, CancellationToken cancellationToken)
    {
        using var request = Request(id);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        var client = factory.CreateClient(ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return null;
        if (!response.IsSuccessStatusCode) return null;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        // An HTML body where a file was asked for is Drive refusing, not a file that happens to be
        // a web page. The download path turns the same signal into a quota message.
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

        var total = response.Content.Headers.ContentRange?.Length
            ?? response.Content.Headers.ContentLength ?? 0;
        return new DriveFile(id, FileNameFrom(response, id), contentType, total);
    }

    /// <summary>
    /// The files inside a shared folder. Drive renders the listing into a script blob rather than
    /// markup, so rather than parse that, every id-shaped token on the page is probed and the ones
    /// that answer as real files are kept. Slower, but it cannot be broken by a layout change.
    /// </summary>
    public async Task<IReadOnlyList<DriveFile>> ListFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        var client = factory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://drive.google.com/drive/folders/{Uri.EscapeDataString(folderId)}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"That Drive folder could not be opened ({(int)response.StatusCode}).");
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var candidates = DriveIdRegex().Matches(html).Select(x => x.Groups[1].Value)
            .Where(x => x != folderId).Distinct(StringComparer.Ordinal).Take(60).ToList();
        var files = new List<DriveFile>();
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await ProbeAsync(id, cancellationToken) is { } file) files.Add(file);
            }
            catch (HttpRequestException ex) { logger.LogDebug(ex, "Drive probe failed for {Id}", id); }
        }
        logger.LogInformation("Drive folder {Folder}: {Files} file(s) from {Candidates} candidate id(s)",
            folderId, files.Count, candidates.Count);
        return files;
    }

    /// <summary>
    /// Streams the file to disk, resuming a partial rather than restarting - these run to twelve
    /// gigabytes, so starting over on a dropped connection is not an option.
    /// </summary>
    public async Task DownloadAsync(DriveFile file, string destination, Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        var already = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (already > 0 && file.SizeBytes > 0 && already >= file.SizeBytes) return;

        using var request = Request(file.Id);
        if (already > 0) request.Headers.Range = new RangeHeaderValue(already, null);
        var client = factory.CreateClient(ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new DriveQuotaException(
                $"Google is limiting downloads of “{file.FileName}”. This is a per-file cap it applies to "
                + "heavily shared files, not a problem with this app or your connection, and no downloader "
                + "gets around it. It usually clears within a day. To avoid the wait, open the file in Drive, "
                + "make a copy into your own Drive, and use that link instead.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Drive refused to send “{file.FileName}” ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var append = already > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        await using var target = new FileStream(destination, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[1 << 20];
        var written = append ? already : 0L;
        var lastReport = -1.0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            if (onProgress is null || file.SizeBytes <= 0) continue;
            var fraction = Math.Min(1, (double)written / file.SizeBytes);
            // Reported in whole percents; a callback per megabyte would be a database write per megabyte.
            if (fraction - lastReport < 0.01) continue;
            lastReport = fraction;
            onProgress(fraction);
        }
    }

    private static HttpRequestMessage Request(string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl(id));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    /// <summary>Drive sends the real name in Content-Disposition; the id is a poor substitute.</summary>
    internal static string FileNameFrom(HttpResponseMessage response, string id)
    {
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        name = name?.Trim('"');
        return string.IsNullOrWhiteSpace(name) ? id : WorkspaceService.SafeFileName(name, id);
    }

    [GeneratedRegex(@"^/drive/(?:u/\d+/)?folders/([A-Za-z0-9_-]{10,})")]
    private static partial Regex FolderPathRegex();
    [GeneratedRegex(@"^/file/d/([A-Za-z0-9_-]{10,})")]
    private static partial Regex FilePathRegex();
    /// <summary>Drive ids as they appear quoted in the folder page's data blob.</summary>
    [GeneratedRegex(@"""([A-Za-z0-9_-]{28,44})""")]
    private static partial Regex DriveIdRegex();
}
