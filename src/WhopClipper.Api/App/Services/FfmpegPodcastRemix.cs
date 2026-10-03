using System.Globalization;

namespace WhopClipper.Api.App.Services;

public static partial class FfmpegArgumentBuilder
{
    public static IReadOnlyList<string> BuildPodcastRemix(string videoPath, string voicePath, string? musicPath,
        string outputPath, string stemPath, double duration, double musicStart, double voiceRatio, double musicVolume,
        double duckingDb, bool normalizeAudio)
    {
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(voiceRatio) || voiceRatio < 0
            || !double.IsFinite(musicVolume) || musicVolume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(duration), "The mix duration and levels must be valid.");
        PodcastMusic.ResolveStart(musicStart, null);
        string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-y", "-i", videoPath, "-i", voicePath };
        var graph = new List<string>
        {
            $"[1:a]atrim=duration={N(duration)},asetpts=PTS-STARTPTS,volume={N(voiceRatio)},asplit=2[voicemix][voicepreview]",
        };
        if (musicPath is null) graph.Add("[voicemix]anull[aout]");
        else
        {
            args.AddRange(["-stream_loop", "-1", "-t", N(musicStart + duration), "-i", musicPath]);
            graph.Add("[voicemix]asplit=2[voiceout][voicekey]");
            var normalize = normalizeAudio ? "loudnorm=I=-14:TP=-2," : string.Empty;
            graph.Add($"[2:a]atrim=start={N(musicStart)}:duration={N(duration)},asetpts=PTS-STARTPTS,{normalize}volume={N(musicVolume)}[musicbase]");
            graph.Add($"[musicbase][voicekey]sidechaincompress=threshold=0.02:ratio={N(Math.Clamp(Math.Abs(duckingDb) / 2, 4, 20))}:attack=5:release=300[musicducked]");
            graph.Add("[voiceout][musicducked]amix=inputs=2:duration=first:dropout_transition=2:normalize=0,alimiter=limit=0.9[aout]");
        }
        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "0:v:0", "-map", "[aout]",
            "-t", N(duration), "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", outputPath,
            "-map", "0:v:0", "-map", "[voicepreview]", "-t", N(duration), "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-ar", "48000", "-movflags", "+faststart", stemPath]);
        return args;
    }
}
