using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed class AppOptions
{
    public string[] AllowedOrigins { get; set; } = ["http://localhost:5173"];
    public string WorkspaceRoot { get; set; } = "./workspace";
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string ClaudePath { get; set; } = "claude";
    public string CodexPath { get; set; } = "codex";
    /// <summary>rclone, which "Move to Google Drive" uploads through. Empty or "rclone" also looks in
    /// WinGet's Links folder, where `winget install Rclone.Rclone` puts it.</summary>
    public string RclonePath { get; set; } = "rclone";
    /// <summary>The rclone remote and folder finished clips move to; each campaign gets a subfolder. The
    /// YtPipeline app already configured the "gdrive" remote on this PC.</summary>
    public string DriveRemote { get; set; } = "gdrive:WhopClipper";
    public bool AutoMigrate { get; set; } = true;
    public int ProcessTimeoutMinutes { get; set; } = 30;
    public int MaxCapturedLogCharacters { get; set; } = 200_000;
    public AiMode DefaultAiMode { get; set; } = AiMode.Compare;
    public int MaxConcurrentMediaJobs { get; set; } = 1;
    /// <summary>Re-run the database's Queued/Running jobs at startup. A second instance started beside the
    /// desktop app (to try a change against real data) must set this false, or it runs the app's own
    /// queue in parallel with it.</summary>
    public bool RestoreQueuedJobs { get; set; } = true;
    public bool RequireEditApproval { get; set; } = true;
    public string PreferredVideoQuality { get; set; } = "Best";
    /// <summary>Browser whose cookies yt-dlp should reuse, e.g. "firefox" or "brave". Empty disables it.</summary>
    public string? CookiesFromBrowser { get; set; }
    /// <summary>Path to a Netscape-format cookies.txt, used when no browser is configured.</summary>
    public string? CookiesFilePath { get; set; }
    /// <summary>JavaScript runtime yt-dlp uses for YouTube. Empty auto-detects one on PATH.</summary>
    public string? JsRuntime { get; set; }
    /// <summary>Python used for faster-whisper transcription. Empty falls back to "python" on PATH.</summary>
    public string? PythonPath { get; set; }
    /// <summary>
    /// Lets the narrated style read its narration with the Windows built-in voice when no ElevenLabs key is saved. Off
    /// by default: it sounds robotic, and exists so the style can be tested before a key is entered.
    /// </summary>
    public bool AllowWindowsVoice { get; set; }
    /// <summary>
    /// Caps x264 encoder threads. Each thread holds its own frame buffers, so on a machine with
    /// little free memory a vertical 1080x1920 encode fails at encoder open with nothing but an
    /// x264 malloc error. 0 leaves FFmpeg to pick, which is one thread per core.
    /// </summary>
    public int EncoderThreads { get; set; }
    /// <summary>faster-whisper model size used for word-level transcripts.</summary>
    public string WhisperModel { get; set; } = "small.en";
    /// <summary>Window fed to the model at a time. Smaller windows lower peak memory, which is
    /// what fails first on a machine that is already short of RAM.</summary>
    public int TranscriptionChunkSeconds { get; set; } = 120;
    public string? OverlayFontPath { get; set; }
    /// <summary>
    /// Floor of free space left alone on the workspace drive, whatever the source is worth. A
    /// download that fills the volume does not just lose itself: it takes down every queued run
    /// behind it, and leaves .part files that nothing cleans up.
    /// </summary>
    public long MinimumFreeDiskBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    /// <summary>
    /// Multiplies the estimated source size to cover what briefly coexists with it - yt-dlp's
    /// separate video and audio streams before they are merged, the merged file, the per-clip
    /// intermediates and the rendered clips.
    /// </summary>
    public double DiskHeadroomFactor { get; set; } = 2.5;
}

public static class LocalSettingsFile
{
    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhopClipper", "appsettings.Local.json");

    public static async Task WriteAtomicallyAsync(string json, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(directory, $"appsettings.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

public sealed class SettingsRuntimeState
{
    private int _restartRequired;
    public bool RestartRequired => Volatile.Read(ref _restartRequired) == 1;
    public void MarkRestartRequired() => Interlocked.Exchange(ref _restartRequired, 1);
}

/// <summary>
/// Answers whether a source is worth starting, before anything is downloaded.
///
/// Without this a run does not fail fast: yt-dlp streams gigabytes, dies with "No space left on
/// device" part way, and the next queued source repeats it. Four runs went that way in one
/// morning. Checking first turns each of those into an instant, legible skip.
/// </summary>
public sealed class DiskSpaceGuard(WorkspaceService workspace, IOptions<AppOptions> options)
{
    /// <summary>
    /// Rough bytes per second of video for each quality yt-dlp is asked for, taken from the
    /// campaign's own downloads: a 3.4 hour 720p60 VOD landed at 2.29 GB, which is ~190 KB/s.
    /// Deliberately generous, because under-estimating is what fills the disk.
    /// </summary>
    private static long BytesPerSecond(string quality) => quality.ToLowerInvariant() switch
    {
        "audio" => 20_000,
        "720p" => 200_000,
        "1080p" => 400_000,
        _ => 500_000
    };

    public long Available => new DriveInfo(Path.GetPathRoot(workspace.Root)!).AvailableFreeSpace;

    /// <summary>Bytes this source is expected to need, including everything alongside it.</summary>
    public long Estimate(double? durationSeconds, string? quality)
    {
        // An unknown duration is the dangerous case, not the harmless one: assume a long VOD
        // rather than waving it through and discovering the truth mid-download.
        var seconds = durationSeconds is > 0 ? durationSeconds.Value : 3 * 60 * 60;
        var raw = (long)(seconds * BytesPerSecond(quality ?? options.Value.PreferredVideoQuality));
        return (long)(raw * Math.Max(1, options.Value.DiskHeadroomFactor));
    }

    /// <summary>
    /// Throws when the source cannot fit. The message carries the actual numbers, because
    /// "No space left on device" three stages deep in a yt-dlp log tells nobody what to free.
    /// </summary>
    /// <summary>
    /// The same check when the exact size is already known, which beats estimating from a duration.
    /// Still multiplied by the headroom factor: the download and whatever is cut from it coexist.
    /// A file that is small and deleted within minutes can pass its own, smaller <paramref name="reserveBytes"/>.
    /// </summary>
    public void EnsureRoomForBytes(long sizeBytes, string fileName, long? reserveBytes = null)
    {
        var needed = (long)(sizeBytes * Math.Max(1, options.Value.DiskHeadroomFactor));
        var free = Available;
        var floor = reserveBytes ?? options.Value.MinimumFreeDiskBytes;
        if (free - needed >= floor) return;
        throw new InvalidOperationException(
            $"Not enough disk for “{fileName}”. It is {Gb(sizeBytes)} and needs about {Gb(needed)} with "
            + $"working room, plus a {Gb(floor)} reserve, and {Path.GetPathRoot(workspace.Root)} has {Gb(free)} free. "
            + "Free some space and queue this source again; the other sources were not affected.");
    }

    public void EnsureRoomFor(double? durationSeconds, string? quality)
    {
        var needed = Estimate(durationSeconds, quality);
        var free = Available;
        var floor = options.Value.MinimumFreeDiskBytes;
        if (free - needed >= floor) return;
        throw new InvalidOperationException(
            $"Not enough disk to process this source. It needs about {Gb(needed)} plus a {Gb(floor)} " +
            $"reserve, and {Path.GetPathRoot(workspace.Root)} has {Gb(free)} free. " +
            "Free some space and queue this source again; the other sources were not affected.");
    }

    private static string Gb(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
}

public sealed class WorkspaceService
{
    public string Root { get; }
    public WorkspaceService(IOptions<AppOptions> options)
    {
        Root = Path.GetFullPath(options.Value.WorkspaceRoot);
        Directory.CreateDirectory(Root);
    }

    public string CampaignDirectory(Guid campaignId)
    {
        var path = ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N")));
        Directory.CreateDirectory(path);
        foreach (var child in new[] { "campaign", "campaign/documents", "reference", "source", "transcript", "analysis/input", "analysis/claude", "analysis/codex", "analysis/comparison", "subtitles", "intermediate", "output", "logs" })
            Directory.CreateDirectory(ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), child)));
        return path;
    }

    public string ResolveRelative(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Expected a workspace-relative path.");
        var resolved = Path.GetFullPath(relativePath, Root);
        EnsureInside(Root, resolved);
        return resolved;
    }

    public string ToRelative(string fullPath)
    {
        var resolved = Path.GetFullPath(fullPath);
        EnsureInside(Root, resolved);
        return Path.GetRelativePath(Root, resolved);
    }

    public static string SafeFileName(string? value, string fallback = "media")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((value ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(name) ? fallback : name[..Math.Min(name.Length, 120)];
    }

    public static void EnsureInside(string root, string target)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !fullTarget.Equals(Path.TrimEndingDirectorySeparator(rootWithSeparator), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved path escapes the configured workspace.");
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public long GetStorageSizeBytes()
    {
        try { return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length); }
        catch (Exception) { return 0; }
    }
}
