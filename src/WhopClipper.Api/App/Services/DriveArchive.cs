using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

/// <summary>One file of a moved clip, as checked in Google Drive.</summary>
public sealed record ArchivedFile(string Name, long Size, string Md5);

/// <summary>
/// Where a moved clip went. Written beside the clip as "&lt;name&gt;.drive.json" before anything is removed, so an
/// interrupted clean-up still leaves a clip that says where its files are, and the gallery keeps listing it.
/// </summary>
/// <param name="SourceIds">For a moved source video, the campaign's source rows that pointed at it, so bringing it back
/// can point them at it again.</param>
public sealed record ArchivedClip(string Remote, IReadOnlyList<ArchivedFile> Files, long Bytes, DateTimeOffset MovedAt,
    DateTimeOffset RenderedAt, IReadOnlyList<Guid>? SourceIds = null);

/// <summary>One file sent to Google Drive: whether Drive already held the same bytes, and its size.</summary>
public sealed record DriveUpload(bool AlreadyThere, long Bytes);

/// <summary>
/// Moves finished clips to Google Drive and back, the way Matthew's YtPipeline app does. It uploads with rclone
/// straight to Google rather than copying into Google Drive for desktop's folder: that app keeps a cached copy on
/// the PC, so moving five videos there once left 2.2 GB behind. Nothing local is removed until every file's size
/// and MD5 match the copy in Drive. Bring back downloads and checks the same way, and leaves the Drive copy alone:
/// deleting it is Matthew's decision.
/// </summary>
public sealed class DriveArchive(IProcessRunner processes, IOptions<AppOptions> options, WorkspaceService workspace,
    ILogger<DriveArchive> logger)
{
    public const string RecordSuffix = ".drive.json";
    public const string PosterSuffix = ".poster.jpg";

    // One transfer at a time: "Move all" sends clips one after another, and two uploads of the same clip must
    // never race each other's clean-up.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string RecordPath(string clipPath) => Path.ChangeExtension(clipPath, null) + RecordSuffix;
    public static string PosterPath(string clipPath) => Path.ChangeExtension(clipPath, null) + PosterSuffix;
    public static string PreviewPath(string clipPath) => Path.ChangeExtension(clipPath, null) + ".preview.mp4";

    /// <summary>The move record of a clip whose files are in Google Drive, or null when they are on this PC.</summary>
    public static ArchivedClip? Read(string clipPath)
    {
        var path = RecordPath(clipPath);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ArchivedClip>(File.ReadAllText(path), JsonDefaults.Serializer); }
        catch (JsonException) { return null; }
    }

    /// <summary>The campaign's folder in Google Drive. Clips go straight into it; source videos into "sources" under it.</summary>
    public string RemoteFor(string campaignFolder) =>
        $"{options.Value.DriveRemote.TrimEnd('/', '\\')}/{WorkspaceService.SafeFileName(campaignFolder, "campaign")}";

    /// <summary>
    /// What a Drive folder holds, by file name, read in one call so a second "Upload all" skips everything already
    /// there without asking about each file. A folder that does not exist yet holds nothing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ArchivedFile>> ListAsync(string remoteFolder, CancellationToken cancellationToken)
    {
        string output;
        try { output = await RcloneAsync(["lsjson", "--files-only", "--hash", remoteFolder], cancellationToken); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, ArchivedFile>();
        }
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
        var files = new Dictionary<string, ArchivedFile>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var file = ReadStat(item);
            files[file.Name] = file;
        }
        return files;
    }

    /// <summary>
    /// Copies one file into a Drive folder and checks its size and MD5 there. The file stays on this PC. A file Drive
    /// already holds byte for byte is not sent again.
    /// </summary>
    /// <param name="known">The file as a listing of the folder showed it, when the caller has one.</param>
    public async Task<DriveUpload> UploadAsync(string remoteFolder, string localPath, ArchivedFile? known,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try { return (await SendAsync(remoteFolder, localPath, known, cancellationToken)).Upload; }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Moves one file that is not a clip, a campaign's source video, to a Drive folder: sent, checked there by size
    /// and MD5, and only then removed from this PC. A "&lt;name&gt;.drive.json" record is left where it was, saying
    /// where it went, and which source rows used it. <see cref="BringBackAsync"/> fetches it again.
    /// </summary>
    public async Task<DriveUpload> MoveFileAsync(string remoteFolder, string localPath, ArchivedFile? known,
        IReadOnlyList<Guid>? sourceIds, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var writtenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath), TimeSpan.Zero);
            var (upload, file) = await SendAsync(remoteFolder, localPath, known, cancellationToken);
            // Recorded before anything is deleted.
            await File.WriteAllTextAsync(RecordPath(localPath), JsonSerializer.Serialize(
                    new ArchivedClip(remoteFolder, [file], file.Size, DateTimeOffset.UtcNow, writtenAt, sourceIds),
                    JsonDefaults.Serializer),
                new UTF8Encoding(false), cancellationToken);
            File.Delete(localPath);
            logger.LogInformation("Moved {File} to {Remote}, freeing {Bytes} bytes.", file.Name, remoteFolder, file.Size);
            return upload;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Sends a file unless Drive already holds the same bytes, and returns it as checked there. The caller
    /// holds the gate.</summary>
    private async Task<(DriveUpload Upload, ArchivedFile File)> SendAsync(string remoteFolder, string localPath,
        ArchivedFile? known, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(localPath);
        var expected = new ArchivedFile(name, new FileInfo(localPath).Length, await Md5Async(localPath, cancellationToken));
        if (known is not null && Same(known, expected)) return (new DriveUpload(true, expected.Size), expected);
        // A long video on a slow line must not be cut off at the half hour that suits a clip: allow a minute for
        // every 20 MB on top of it.
        var limit = TimeSpan.FromMinutes(30 + expected.Size / (20d * 1024 * 1024));
        await RcloneAsync(["copyto", localPath, $"{remoteFolder}/{name}"], cancellationToken, limit);
        var arrived = await StatAsync($"{remoteFolder}/{name}", cancellationToken);
        if (!Same(arrived, expected))
            throw new InvalidOperationException(
                $"{name} did not arrive in Google Drive intact ({arrived.Size} of {expected.Size} bytes). It is still on this PC.");
        return (new DriveUpload(false, expected.Size), expected);
    }

    private static bool Same(ArchivedFile a, ArchivedFile b) =>
        a.Size == b.Size && string.Equals(a.Md5, b.Md5, StringComparison.OrdinalIgnoreCase);

    public async Task<ArchivedClip> MoveOutAsync(string campaignFolder, string clipPath, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(RecordPath(clipPath)))
                throw new InvalidOperationException("This clip is already in Google Drive.");
            var remote = RemoteFor(campaignFolder);
            var renderedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(clipPath), TimeSpan.Zero);
            // The thumbnail stays behind, so the clip is still recognisable in the gallery once its video is gone.
            await MakePosterAsync(clipPath, cancellationToken);

            var files = new List<ArchivedFile>();
            foreach (var local in new[] { clipPath, PreviewPath(clipPath) }.Where(File.Exists))
            {
                var name = Path.GetFileName(local);
                var expected = new ArchivedFile(name, new FileInfo(local).Length, await Md5Async(local, cancellationToken));
                await RcloneAsync(["copyto", local, $"{remote}/{name}"], cancellationToken);
                var arrived = await StatAsync($"{remote}/{name}", cancellationToken);
                if (arrived.Size != expected.Size || !string.Equals(arrived.Md5, expected.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"{name} did not arrive in Google Drive intact ({arrived.Size} of {expected.Size} bytes), so nothing on this PC was removed.");
                files.Add(expected);
            }

            var copy = new ArchivedClip(remote, files, files.Sum(x => x.Size), DateTimeOffset.UtcNow, renderedAt);
            // Recorded before anything is deleted.
            await File.WriteAllTextAsync(RecordPath(clipPath), JsonSerializer.Serialize(copy, JsonDefaults.Serializer),
                new UTF8Encoding(false), cancellationToken);
            foreach (var file in files)
                File.Delete(Path.Combine(Path.GetDirectoryName(clipPath)!, file.Name));
            logger.LogInformation("Moved {Clip} to {Remote}, freeing {Bytes} bytes.", Path.GetFileName(clipPath), remote, copy.Bytes);
            return copy;
        }
        finally { Gate.Release(); }
    }

    public async Task BringBackAsync(string clipPath, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var copy = Read(clipPath) ?? throw new InvalidOperationException("This clip is already on this PC.");
            var folder = Path.GetDirectoryName(clipPath)!;
            foreach (var file in copy.Files)
            {
                var local = Path.Combine(folder, file.Name);
                // A retry after an interrupted download skips what already arrived intact.
                if (!File.Exists(local) || new FileInfo(local).Length != file.Size ||
                    !string.Equals(await Md5Async(local, cancellationToken), file.Md5, StringComparison.OrdinalIgnoreCase))
                    await RcloneAsync(["copyto", $"{copy.Remote}/{file.Name}", local], cancellationToken);
                if (new FileInfo(local).Length != file.Size ||
                    !string.Equals(await Md5Async(local, cancellationToken), file.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{file.Name} did not come back intact. It is still in Google Drive; try again.");
            }
            File.Delete(RecordPath(clipPath));
            if (File.Exists(PosterPath(clipPath))) File.Delete(PosterPath(clipPath));
            logger.LogInformation("Brought {Clip} back from {Remote}.", Path.GetFileName(clipPath), copy.Remote);
        }
        finally { Gate.Release(); }
    }

    private async Task MakePosterAsync(string clipPath, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessInvocation(options.Value.FfmpegPath,
            ["-y", "-v", "error", "-ss", "1", "-i", clipPath, "-frames:v", "1", "-vf", "scale=360:-2", PosterPath(clipPath)],
            workspace.Root, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        // Only the gallery's picture depends on it; a clip without one still moves.
        if (result.ExitCode != 0) logger.LogWarning("No thumbnail for {Clip}: {Error}", clipPath, result.StandardError);
    }

    private async Task<ArchivedFile> StatAsync(string remotePath, CancellationToken cancellationToken)
    {
        var output = await RcloneAsync(["lsjson", "--stat", "--hash", remotePath], cancellationToken);
        using var document = JsonDocument.Parse(output);
        return ReadStat(document.RootElement);
    }

    private static ArchivedFile ReadStat(JsonElement item)
    {
        var md5 = item.TryGetProperty("Hashes", out var hashes) && hashes.TryGetProperty("md5", out var value)
            ? value.GetString() ?? "" : "";
        return new ArchivedFile(item.GetProperty("Name").GetString() ?? "", item.GetProperty("Size").GetInt64(), md5);
    }

    private async Task<string> RcloneAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await processes.RunAsync(new ProcessInvocation(RcloneExecutable(), arguments, workspace.Root,
            Timeout: timeout ?? TimeSpan.FromMinutes(30)), cancellationToken);
        if (result.ExitCode == 0 && !result.TimedOut) return result.StandardOutput;
        if (result.ExitCode == -1 && result.StandardOutput.Length == 0)
            throw new InvalidOperationException("rclone isn't installed, so nothing was moved. Install it with: winget install Rclone.Rclone");
        // rclone puts the reason on its last error line ("didn't find section in config file"); a NOTICE about the
        // shared Google client is only a warning and never the reason.
        var why = result.StandardError.Split('\n').Select(x => x.Trim())
            .LastOrDefault(x => x.Length > 0 && !x.Contains("NOTICE", StringComparison.Ordinal))
            ?? (result.TimedOut ? "it timed out" : $"exit code {result.ExitCode}");
        throw new InvalidOperationException($"rclone {arguments[0]} failed, and nothing on this PC was removed: {why}");
    }

    private string RcloneExecutable()
    {
        var configured = options.Value.RclonePath;
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("rclone", StringComparison.OrdinalIgnoreCase))
            return configured;
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "rclone.exe");
        return File.Exists(winget) ? winget : "rclone";
    }

    private static async Task<string> Md5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellationToken));
    }
}
