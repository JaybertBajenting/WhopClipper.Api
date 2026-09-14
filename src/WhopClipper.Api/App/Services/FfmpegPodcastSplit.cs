using System.Globalization;
using System.Text;

namespace WhopClipper.Api.App.Services;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool FitsInside(int width, int height) =>
        X >= 0 && Y >= 0 && Width >= 2 && Height >= 2 && X + Width <= width && Y + Height <= height;
}

/// <param name="Blur">Logo boxes left inside the crop, in top-half output pixels (1080x960).</param>
public sealed record FfmpegTopShot(double InSeconds, double DurationSeconds, PixelRect Crop,
    IReadOnlyList<PixelRect>? Blur = null);

/// <param name="Blur">Logo boxes left inside the crop, in bottom-half output pixels (1080x960).</param>
public sealed record FfmpegBrollShot(string Path, double InSeconds, double DurationSeconds, PixelRect Crop,
    int SourceWidth, int SourceHeight, IReadOnlyList<PixelRect>? Blur = null);

/// <summary>
/// A podcast-split render. The top half is cut from one downloaded span of the episode, one
/// input per shot so every piece seeks straight to its own moment; the bottom half is the B-roll
/// shots laid end to end for exactly the same length.
/// </summary>
public sealed record FfmpegPodcastSplitSpec(string SpanPath, int SpanWidth, int SpanHeight, string OutputPath,
    int FramesPerSecond, int Crf, IReadOnlyList<FfmpegTopShot> TopShots, IReadOnlyList<FfmpegBrollShot> Broll,
    string? SubtitlePath = null, string? MusicPath = null, double MusicVolume = 0.3, double MusicDuckingDb = -10,
    FfmpegLogoOverlay? Logo = null, double LogoRightMarginFraction = 0.074, double LogoTopMarginFraction = 0.071,
    bool NormalizeAudio = true, int EncoderThreads = 0);

public static partial class FfmpegArgumentBuilder
{
    public const int SplitWidth = 1080;
    public const int SplitHeight = 1920;
    public const int SplitHalf = 960;

    /// <summary>
    /// Builds the Double Coverage TV layout. Measured across 25 of the channel's shorts: the frame
    /// splits at exactly y 960, the guest's camera is a fixed punch-in (about 960x853 of a
    /// 1920x1080 frame), and the footage underneath runs for the whole clip.
    ///
    /// The halves are stacked with pad and overlay rather than vstack, which segfaults in the
    /// bundled FFmpeg build. There are no zooms, washes or sound cues: that is the other style.
    /// </summary>
    public static IReadOnlyList<string> BuildPodcastSplit(FfmpegPodcastSplitSpec spec)
    {
        ValidatePodcastSplit(spec);
        var invariant = CultureInfo.InvariantCulture;
        string Number(double value) => value.ToString("0.###", invariant);

        var total = spec.TopShots.Sum(x => x.DurationSeconds);
        var args = new List<string> { "-hide_banner", "-y" };
        foreach (var shot in spec.TopShots)
            args.AddRange(["-ss", Number(shot.InSeconds), "-t", Number(shot.DurationSeconds), "-i", spec.SpanPath]);

        var nextInput = spec.TopShots.Count;
        var brollInputs = new List<int>();
        foreach (var shot in spec.Broll)
        {
            brollInputs.Add(nextInput++);
            // Half a second of slack: a section download can end a frame or two short of what was
            // asked for, and tpad below covers whatever is still missing.
            args.AddRange(["-ss", Number(shot.InSeconds), "-t", Number(shot.DurationSeconds + 0.5), "-i", shot.Path]);
        }

        int? musicInput = null;
        if (!string.IsNullOrWhiteSpace(spec.MusicPath))
        {
            musicInput = nextInput++;
            args.AddRange(["-stream_loop", "-1", "-t", Number(total), "-i", spec.MusicPath]);
        }

        int? logoInput = null;
        if (spec.Logo is not null)
        {
            logoInput = nextInput++;
            args.AddRange(["-i", spec.Logo.Path]);
        }

        var fps = spec.FramesPerSecond.ToString(invariant);
        var graph = new List<string>();
        var joined = new StringBuilder();
        for (var index = 0; index < spec.TopShots.Count; index++)
        {
            var shot = spec.TopShots[index];
            var filters = new List<string>
            {
                $"crop={shot.Crop.Width}:{shot.Crop.Height}:{shot.Crop.X}:{shot.Crop.Y}",
                $"scale={SplitWidth}:{SplitHalf}", "setsar=1", $"fps={fps}"
            };
            filters.AddRange(Delogo(shot.Blur));
            filters.Add("setpts=PTS-STARTPTS");
            graph.Add($"[{index}:v]{string.Join(',', filters)}[tv{index}]");
            graph.Add($"[{index}:a]aresample=48000,asetpts=PTS-STARTPTS[ta{index}]");
            joined.Append($"[tv{index}][ta{index}]");
        }
        // The condensed story: every piece's picture and sound joined in order, so the cuts in
        // the voice and the cuts in the camera can never drift apart.
        graph.Add($"{joined}concat=n={spec.TopShots.Count}:v=1:a=1[top][voice0]");

        var topLabel = "top";
        if (spec.Broll.Count == 0)
        {
            // Nothing usable was found for any moment. A blurred copy of the guest keeps the
            // layout intact instead of leaving half the frame black.
            graph.Add("[top]split=2[topmain][topcopy]");
            graph.Add("[topcopy]boxblur=luma_radius=40:luma_power=2:chroma_radius=20:chroma_power=2,eq=brightness=-0.08[bottom]");
            topLabel = "topmain";
        }
        else
        {
            var labels = new StringBuilder();
            for (var index = 0; index < spec.Broll.Count; index++)
            {
                var shot = spec.Broll[index];
                var filters = new List<string>
                {
                    $"crop={shot.Crop.Width}:{shot.Crop.Height}:{shot.Crop.X}:{shot.Crop.Y}",
                    $"scale={SplitWidth}:{SplitHalf}", "setsar=1", $"fps={fps}"
                };
                filters.AddRange(Delogo(shot.Blur));
                filters.Add($"tpad=stop_mode=clone:stop_duration={Number(shot.DurationSeconds)}");
                filters.Add($"trim=duration={Number(shot.DurationSeconds)}");
                filters.Add("setpts=PTS-STARTPTS");
                graph.Add($"[{brollInputs[index]}:v]{string.Join(',', filters)}[bv{index}]");
                labels.Append($"[bv{index}]");
            }
            graph.Add($"{labels}concat=n={spec.Broll.Count}:v=1:a=0[bottom]");
        }

        graph.Add($"[{topLabel}]pad={SplitWidth}:{SplitHeight}:0:0:color=black[bg]");
        var stacked = $"[bg][bottom]overlay=0:{SplitHalf}:eof_action=repeat,setpts=PTS-STARTPTS";
        if (!string.IsNullOrWhiteSpace(spec.SubtitlePath))
            stacked += $",subtitles='{EscapeFilterPath(spec.SubtitlePath)}'";
        graph.Add($"{stacked}[vsplit]");

        const string deliveryFormat = "format=yuv420p,setsar=1";
        if (logoInput is null)
            graph.Add($"[vsplit]{deliveryFormat}[vout]");
        else
        {
            // Top-right of the guest half, where the channel puts its own logo: clear of the
            // B-roll and of the caption on the split line, and never over anyone's face.
            var logo = spec.Logo!;
            var logoWidth = Math.Max(2, (int)Math.Round(SplitWidth * logo.WidthFraction / 2) * 2);
            var logoFilters = new List<string>();
            if (logo.CropRect is not null) logoFilters.Add($"crop={logo.CropRect}");
            logoFilters.Add($"scale={logoWidth}:-1");
            logoFilters.Add("format=rgba");
            if (logo.Opacity < 1) logoFilters.Add($"colorchannelmixer=aa={Number(logo.Opacity)}");
            graph.Add($"[{logoInput.Value}:v]{string.Join(',', logoFilters)}[vlogo]");
            graph.Add($"[vsplit][vlogo]overlay=W-w-W*{spec.LogoRightMarginFraction.ToString("0.####", invariant)}:" +
                      $"H*{spec.LogoTopMarginFraction.ToString("0.####", invariant)}:format=auto,{deliveryFormat}[vout]");
        }

        var loudness = spec.NormalizeAudio ? "loudnorm=I=-14:TP=-1.5:LRA=11" : "anull";
        if (musicInput is null)
            graph.Add($"[voice0]{loudness}[aout]");
        else
        {
            var ratio = Math.Clamp(Math.Abs(spec.MusicDuckingDb) / 2d, 2d, 20d).ToString("0.##", invariant);
            graph.Add("[voice0]asplit=2[voice][voicekey]");
            graph.Add($"[{musicInput.Value}:a]atrim=0:{Number(total)},asetpts=PTS-STARTPTS,volume={Number(spec.MusicVolume)}[musicbase]");
            graph.Add($"[musicbase][voicekey]sidechaincompress=threshold=0.05:ratio={ratio}:attack=20:release=500[musicducked]");
            graph.Add($"[voice][musicducked]amix=inputs=2:duration=first:dropout_transition=2" +
                      (spec.NormalizeAudio ? $",{loudness}" : string.Empty) + "[aout]");
        }

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]", "-map", "[aout]",
            "-t", Number(total), "-c:v", "libx264", "-preset", "medium", "-crf", spec.Crf.ToString(invariant)]);
        if (spec.EncoderThreads > 0) args.AddRange(["-threads", spec.EncoderThreads.ToString(invariant)]);
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", spec.OutputPath]);
        return args;
    }

    private static void ValidatePodcastSplit(FfmpegPodcastSplitSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.SpanPath) || string.IsNullOrWhiteSpace(spec.OutputPath))
            throw new ArgumentException("The span and output paths are required.", nameof(spec));
        if (spec.SpanWidth is < 240 or > 7680 || spec.SpanHeight is < 240 or > 7680)
            throw new ArgumentOutOfRangeException(nameof(spec), "The span dimensions are not a real video size.");
        if (spec.FramesPerSecond is < 12 or > 120) throw new ArgumentOutOfRangeException(nameof(spec.FramesPerSecond));
        if (spec.Crf is < 0 or > 51) throw new ArgumentOutOfRangeException(nameof(spec.Crf));
        if (spec.MusicVolume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.MusicVolume));
        if (spec.MusicDuckingDb is < -40 or > 0) throw new ArgumentOutOfRangeException(nameof(spec.MusicDuckingDb));
        if (spec.LogoRightMarginFraction is < 0 or > 0.45 || spec.LogoTopMarginFraction is < 0 or > 0.45)
            throw new ArgumentOutOfRangeException(nameof(spec), "Logo margins must be between 0 and 0.45 of the frame.");
        if (spec.TopShots.Count is < 1 or > 40)
            throw new ArgumentException("A podcast split needs between 1 and 40 camera shots.", nameof(spec));
        if (spec.Broll.Count > 80) throw new ArgumentException("Too many B-roll shots.", nameof(spec));

        foreach (var shot in spec.TopShots)
        {
            if (shot.InSeconds < 0 || shot.DurationSeconds <= 0.05)
                throw new ArgumentOutOfRangeException(nameof(spec.TopShots), "A camera shot needs a start and a length.");
            if (!shot.Crop.FitsInside(spec.SpanWidth, spec.SpanHeight))
                throw new ArgumentException($"Camera crop {shot.Crop} falls outside the {spec.SpanWidth}x{spec.SpanHeight} span.", nameof(spec.TopShots));
            ValidateBlur(shot.Blur, nameof(spec.TopShots));
        }

        var total = spec.TopShots.Sum(x => x.DurationSeconds);
        if (total is < 1 or > 180) throw new ArgumentOutOfRangeException(nameof(spec.TopShots), "A clip must run 1-180 seconds.");

        foreach (var shot in spec.Broll)
        {
            if (string.IsNullOrWhiteSpace(shot.Path) || shot.InSeconds < 0 || shot.DurationSeconds <= 0.05)
                throw new ArgumentOutOfRangeException(nameof(spec.Broll), "A B-roll shot needs a file, a start and a length.");
            if (!shot.Crop.FitsInside(shot.SourceWidth, shot.SourceHeight))
                throw new ArgumentException($"B-roll crop {shot.Crop} falls outside its {shot.SourceWidth}x{shot.SourceHeight} source.", nameof(spec.Broll));
            ValidateBlur(shot.Blur, nameof(spec.Broll));
        }
        // The two halves are overlaid, so a bottom half that ends early would freeze on its last
        // frame and one that runs long would be cut. Either means the planning went wrong.
        if (spec.Broll.Count > 0 && Math.Abs(spec.Broll.Sum(x => x.DurationSeconds) - total) > 0.1)
            throw new ArgumentException("The B-roll must cover exactly the length of the clip.", nameof(spec.Broll));

        if (spec.Logo is { } logo)
        {
            if (logo.WidthFraction is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.Logo));
            if (logo.Opacity is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.Logo));
            if (logo.CropRect is not null && !CropRectPattern().IsMatch(logo.CropRect))
                throw new ArgumentException("Invalid logo crop rectangle.", nameof(spec.Logo));
        }
    }

    private static void ValidateBlur(IReadOnlyList<PixelRect>? rects, string name)
    {
        foreach (var rect in rects ?? [])
            if (!rect.FitsInside(SplitWidth, SplitHalf))
                throw new ArgumentException($"Blur box {rect} falls outside the half frame.", name);
    }

    /// <summary>
    /// delogo rebuilds the box from the pixels around it, so it has to leave at least one row and
    /// column of picture on every side - a box touching the edge makes FFmpeg refuse the graph.
    /// </summary>
    private static IEnumerable<string> Delogo(IReadOnlyList<PixelRect>? rects)
    {
        foreach (var rect in rects ?? [])
        {
            var x = Math.Clamp(rect.X, 1, SplitWidth - 4);
            var y = Math.Clamp(rect.Y, 1, SplitHalf - 4);
            var width = Math.Min(rect.Width, SplitWidth - 2 - x);
            var height = Math.Min(rect.Height, SplitHalf - 2 - y);
            if (width < 2 || height < 2) continue;
            yield return $"delogo=x={x}:y={y}:w={width}:h={height}";
        }
    }
}
