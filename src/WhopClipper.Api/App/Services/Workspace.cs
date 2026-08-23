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
    public bool AutoMigrate { get; set; } = true;
    public int ProcessTimeoutMinutes { get; set; } = 30;
    public int MaxCapturedLogCharacters { get; set; } = 200_000;
    public AiMode DefaultAiMode { get; set; } = AiMode.Compare;
    public int MaxConcurrentMediaJobs { get; set; } = 1;
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
