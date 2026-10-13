using System.Globalization;
using System.Text;

namespace WhopClipper.Api.App.Services;

/// <param name="FocusX">Where the subject sits across the frame, 0 to 1. The window shows about half the width.</param>
/// <param name="Payoff">Its own sound plays. Montage shots are silent under the narrator.</param>
public sealed record FfmpegNarratedShot(string Path, double InSeconds, double DurationSeconds, double FocusX, bool Payoff);

public sealed record FfmpegNarratedImage(string Path, int Width, int Height);

/// <summary>A point on a shot's 16:9 frame as fractions, and a face width as a fraction of the frame width.</summary>
public sealed record FfmpegNarratedMark(double X, double Y, double Size);

/// <summary>A sound effect laid over the mix: a file already at its playing level, and when it starts.</summary>
public sealed record FfmpegNarratedSound(string Path, double AtSeconds, double Volume = 1.0);

/// <summary>A silent shot laid over the moment's picture while the moment's sound carries on.</summary>
/// <param name="AtSeconds">When it starts on the clip's clock.</param>
public sealed record FfmpegNarratedCutaway(string Path, double InSeconds, double AtSeconds, double DurationSeconds, double FocusX);

/// <summary>
/// A narrated-luxury render: montage shots under the narration, then the payoff pieces with their own sound. The
/// arrow points at <see cref="ArrowTarget"/> on the first shot while the window opens; the skull covers
/// <see cref="SkullTarget"/> on the frozen last frame.
/// </summary>
public sealed record FfmpegNarratedSpec(IReadOnlyList<FfmpegNarratedShot> Shots, string NarrationPath, double NarrationSeconds,
    string OutputPath, int FramesPerSecond, int Crf, FfmpegNarratedImage Title, string? SubtitlePath = null,
    FfmpegNarratedImage? Arrow = null, int ArrowTipX = 0, int ArrowTipY = 0, FfmpegNarratedMark? ArrowTarget = null,
    FfmpegNarratedImage? Skull = null, FfmpegNarratedMark? SkullTarget = null, string? MusicPath = null,
    double MusicStartSeconds = 0, double MusicVolume = 0.4, double VoiceVolume = 1.0, bool NormalizeAudio = true,
    int EncoderThreads = 0, double FreezeSeconds = FfmpegArgumentBuilder.NarratedFreezeSeconds, double? MusicDropVolume = null,
    IReadOnlyList<FfmpegNarratedSound>? Sounds = null, IReadOnlyList<FfmpegNarratedCutaway>? Cutaways = null);

public static partial class FfmpegArgumentBuilder
{
    // Measured on 16 @mambafx.luxury shorts: the picture settles into a 1080x1148 window whose centre is at y 935,
    // after opening on the whole 16:9 frame and growing for about a second and three quarters.
    public const int NarratedWindowHeight = 1148;
    public const int NarratedWindowCentre = 935;
    public const int NarratedWindowTop = NarratedWindowCentre - NarratedWindowHeight / 2;
    public const double NarratedIntroSeconds = 1.75;
    /// <summary>How long the last frame holds under the skull. In 13 of 16 reference shorts the music jumps 20 dB or
    /// more exactly 0.60 s before the end: the freeze is the song's drop.</summary>
    public const double NarratedFreezeSeconds = 0.6;
    /// <summary>The music's gain while anyone speaks, as a share of its gain at the drop: about 8 dB down.</summary>
    public const double NarratedUnderSpeechGain = 0.4;
    /// <summary>Every shot is first made 1920x1080; the window then shows this much of its width.</summary>
    public const int NarratedVisibleWidth = 1016;

    private const double StartScale = 1080d / 1920d;
    private const double EndScale = (double)NarratedWindowHeight / 1080d;

    /// <summary>Where the window's left edge of the 1920-wide frame sits so the subject is centred, in source pixels.</summary>
    public static int NarratedCropX(double focusX)
    {
        var x = (int)Math.Round((double.IsFinite(focusX) ? Math.Clamp(focusX, 0, 1) : 0.5) * 1920 - NarratedVisibleWidth / 2d);
        x = Math.Clamp(x, 0, 1920 - NarratedVisibleWidth);
        return x - x % 2;
    }

    public static IReadOnlyList<string> BuildNarrated(FfmpegNarratedSpec spec)
    {
        ValidateNarrated(spec);
        var invariant = CultureInfo.InvariantCulture;
        string N(double value) => value.ToString("0.#####", invariant);
        var fps = spec.FramesPerSecond.ToString(invariant);
        var payoffSeconds = spec.Shots.Where(x => x.Payoff).Sum(x => x.DurationSeconds);
        var speech = spec.NarrationSeconds + payoffSeconds;
        var total = speech + spec.FreezeSeconds;
        var args = new List<string> { "-hide_banner", "-y" };
        foreach (var shot in spec.Shots)
            // Half a second of slack: a section download can end a frame short, and tpad covers any shortfall.
            args.AddRange(["-ss", N(shot.InSeconds), "-t", N(shot.DurationSeconds + 0.5), "-i", shot.Path]);
        var next = spec.Shots.Count;
        var narrationInput = next++;
        args.AddRange(["-i", spec.NarrationPath]);
        int? musicInput = null;
        if (!string.IsNullOrWhiteSpace(spec.MusicPath))
        {
            musicInput = next++;
            args.AddRange(["-stream_loop", "-1", "-t", N(spec.MusicStartSeconds + total), "-i", spec.MusicPath]);
        }
        var titleInput = next++;
        args.AddRange(["-i", spec.Title.Path]);
        int? arrowInput = null;
        if (spec.Arrow is not null && spec.ArrowTarget is not null)
        {
            arrowInput = next++;
            args.AddRange(["-i", spec.Arrow.Path]);
        }
        int? skullInput = null;
        if (spec.Skull is not null && spec.SkullTarget is not null)
        {
            skullInput = next++;
            args.AddRange(["-i", spec.Skull.Path]);
        }
        var sounds = (spec.Sounds ?? []).Where(x => x.AtSeconds < total).ToList();
        var firstSoundInput = next;
        foreach (var sound in sounds) args.AddRange(["-i", sound.Path]);
        var cutaways = spec.Cutaways ?? [];
        var firstCutawayInput = firstSoundInput + sounds.Count;
        foreach (var cutaway in cutaways)
            args.AddRange(["-ss", N(cutaway.InSeconds), "-t", N(cutaway.DurationSeconds + 0.5), "-i", cutaway.Path]);

        // The window grows while the first shot plays: P runs 0 to 1 over the intro with a gentle ease-out, and the
        // picture's scale K runs from "whole frame across 1080" to "1080 tall filling 1148". Written for both scale
        // (whose clock is t) and overlay (also t), with commas escaped for the filter graph.
        var intro = Math.Min(NarratedIntroSeconds, spec.Shots[0].DurationSeconds);
        var p = $"(1-pow(1-min(t/{N(intro)}\\,1)\\,1.3))";
        var k = $"({N(StartScale)}+{N(EndScale - StartScale)}*{p})";
        var firstCrop = NarratedCropX(spec.Shots[0].FocusX);
        var x0 = $"(-{firstCrop}*{k}*{p})";
        var top = $"({NarratedWindowCentre}-540*{k})";
        const string normalise = "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,setsar=1";
        const string grade = "eq=saturation=1.22:contrast=1.06";

        var graph = new List<string>();
        var joined = new StringBuilder();
        for (var index = 0; index < spec.Shots.Count; index++)
        {
            var shot = spec.Shots[index];
            var hold = $"tpad=stop_mode=clone:stop_duration={N(shot.DurationSeconds)},trim=duration={N(shot.DurationSeconds)},setpts=PTS-STARTPTS";
            if (index == 0)
            {
                graph.Add($"[0:v]{normalise},fps={fps},{grade},{hold}," +
                          $"scale=w='2*trunc(960*{k})':h='2*trunc(540*{k})':eval=frame[s0]");
                graph.Add($"color=c=black:s={SplitWidth}x{SplitHeight}:r={fps}:d={N(shot.DurationSeconds)}[bg0]");
                graph.Add($"[bg0][s0]overlay=x='{x0}':y='{NarratedWindowCentre}-overlay_h/2':eval=frame:shortest=1,setsar=1[v0]");
            }
            else
            {
                // The hand-over from narrator to the moment itself lands on a white exposure flash, as the reference
                // does: on the picture only, so the black canvas around the window stays black.
                var flash = shot.Payoff && !spec.Shots[index - 1].Payoff
                    ? ",eq=brightness=0.3:saturation=0.7:enable='lt(t\\,0.12)'" : string.Empty;
                graph.Add($"[{index}:v]{normalise},crop={NarratedVisibleWidth}:1080:{NarratedCropX(shot.FocusX)}:0," +
                          $"scale={SplitWidth}:{NarratedWindowHeight},setsar=1,fps={fps},{grade},{hold}{flash}," +
                          $"pad={SplitWidth}:{SplitHeight}:0:{NarratedWindowTop}:color=black[v{index}]");
            }
            joined.Append($"[v{index}]");
        }
        graph.Add($"{joined}concat=n={spec.Shots.Count}:v=1:a=0[pic]");

        // Cutaways cover the window for a beat while the moment's sound plays on underneath. Each is put on the
        // clip's clock and laid over the joined picture; before it starts and once it has ended the overlay passes
        // the moment through untouched. Captions, the title and the skull are drawn afterwards, so they stay on top.
        var joinedPicture = "pic";
        for (var index = 0; index < cutaways.Count; index++)
        {
            var cutaway = cutaways[index];
            var length = N(cutaway.DurationSeconds);
            graph.Add($"[{firstCutawayInput + index}:v]{normalise},crop={NarratedVisibleWidth}:1080:{NarratedCropX(cutaway.FocusX)}:0," +
                      $"scale={SplitWidth}:{NarratedWindowHeight},setsar=1,fps={fps},{grade}," +
                      $"tpad=stop_mode=clone:stop_duration={length},trim=duration={length}," +
                      $"setpts=PTS-STARTPTS+{N(cutaway.AtSeconds)}/TB[cut{index}]");
            graph.Add($"[{joinedPicture}][cut{index}]overlay=x=0:y={NarratedWindowTop}:eof_action=pass:" +
                      $"enable='between(t\\,{N(cutaway.AtSeconds)}\\,{N(cutaway.AtSeconds + cutaway.DurationSeconds)})'[cutpic{index}]");
            joinedPicture = $"cutpic{index}";
        }

        var picture = $"[{joinedPicture}]tpad=stop_mode=clone:stop_duration={N(spec.FreezeSeconds)},hue=s=0:enable='gte(t\\,{N(speech)})'";
        if (!string.IsNullOrWhiteSpace(spec.SubtitlePath)) picture += $",subtitles='{EscapeFilterPath(spec.SubtitlePath)}'";
        graph.Add($"{picture}[captioned]");

        var stage = "captioned";
        if (skullInput is { } skull && spec.SkullTarget is { } face)
        {
            // Over the face on the frozen last frame, which sits in the settled window.
            var last = spec.Shots[^1];
            // About one and a half faces wide, as the reference: a close-up gets a big skull, never a wall of one.
            var size = Math.Clamp((int)Math.Round(face.Size * 1920 * EndScale * 1.5), 240, 520);
            size -= size % 2;
            var cx = (face.X * 1920 - NarratedCropX(last.FocusX)) * EndScale;
            var cy = NarratedWindowTop + face.Y * 1080 * EndScale;
            graph.Add($"[{skull}:v]scale={size}:-1,format=rgba[skullimg]");
            graph.Add($"[{stage}][skullimg]overlay=x={N(Math.Round(cx - size / 2d))}:y={N(Math.Round(cy - size * 0.55))}:" +
                      $"enable='gte(t\\,{N(speech)})'[skulled]");
            stage = "skulled";
        }
        if (arrowInput is { } arrow && spec.ArrowTarget is { } target)
        {
            // The tip sits just below-left of the face and rides the growing picture, for the first beat only.
            var faceWidth = Math.Clamp(target.Size, 0.03, 0.4);
            var tipX = $"({x0}+({N(target.X - faceWidth * 0.55)})*1920*{k})";
            var tipY = $"({top}+({N(target.Y + faceWidth * 0.45)})*1080*{k})";
            graph.Add($"[{stage}][{arrow}:v]overlay=x='{tipX}-{spec.ArrowTipX}':y='{tipY}-{spec.ArrowTipY}':eval=frame:" +
                      $"enable='lt(t\\,{N(Math.Min(0.9, intro))})'[arrowed]");
            stage = "arrowed";
        }
        // The title sits on the window's top edge as it grows, then stays for the whole clip.
        graph.Add($"[{stage}][{titleInput}:v]overlay=x=(W-w)/2:y='{top}-h-6':eval=frame,format=yuv420p,setsar=1[vout]");

        // Narrator, then the moment's own sound, each levelled on its own: a studio voice and a vlog microphone
        // are nowhere near the same loudness.
        var loudness = spec.NormalizeAudio ? "loudnorm=I=-14:TP=-1.5:LRA=11,aresample=48000," : string.Empty;
        const string stereo = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";
        graph.Add($"[{narrationInput}:a]{stereo},atrim=0:{N(spec.NarrationSeconds)},apad=whole_dur={N(spec.NarrationSeconds)}," +
                  $"asetpts=PTS-STARTPTS,{loudness}anull[narration]");
        var payoffLabels = new StringBuilder();
        var payoffCount = 0;
        for (var index = 0; index < spec.Shots.Count; index++)
        {
            if (!spec.Shots[index].Payoff) continue;
            var length = N(spec.Shots[index].DurationSeconds);
            graph.Add($"[{index}:a]{stereo},atrim=0:{length},apad=whole_dur={length},asetpts=PTS-STARTPTS[pa{index}]");
            payoffLabels.Append($"[pa{index}]");
            payoffCount++;
        }
        graph.Add($"{payoffLabels}concat=n={payoffCount}:v=0:a=1,{loudness}anull[payoff]");
        graph.Add($"[narration][payoff]concat=n=2:v=0:a=1,apad=pad_dur={N(spec.FreezeSeconds)}," +
                  $"volume={N(spec.VoiceVolume)}[voice]");
        // The reference's two sound effects, the bell with the arrow and the shutter on the hand-over flash. Each
        // file is already at its playing level, so nothing here follows the voice or music sliders.
        var layers = new StringBuilder("[voice]");
        var layerCount = 1;
        for (var index = 0; index < sounds.Count; index++)
        {
            var delay = (int)Math.Round(Math.Max(0, sounds[index].AtSeconds) * 1000);
            graph.Add($"[{firstSoundInput + index}:a]{stereo},adelay={delay}:all=1,volume={N(sounds[index].Volume)}[sfx{index}]");
            layers.Append($"[sfx{index}]");
            layerCount++;
        }
        if (musicInput is null)
            graph.Add(layerCount == 1
                ? "[voice]alimiter=limit=0.9[aout]"
                : $"{layers}amix=inputs={layerCount}:duration=first:dropout_transition=2:normalize=0,alimiter=limit=0.9[aout]");
        else
        {
            // The song keeps its own dynamics: never loudness-normalised, because these songs fade to near silence and
            // slam back, and the clip is timed so that slam is the freeze. Measured on the reference (12 shorts, music
            // split from the voices): under the talking the music sits 8 to 10 dB below where it is at the drop, and
            // at the freeze it is about 8 dB under the song's full level (a gain of 0.4), some 4 dB above the voices.
            // So the gain is low while anyone speaks and steps up to MusicVolume the moment the freeze starts. A
            // steady 0.4 put a loud passage of the song over a quiet vlog line and whisper could no longer hear him.
            // MusicDropVolume, when the song's drop was measured, plays that drop at the reference's loudness whatever
            // the song's mastering; without it the drop uses MusicVolume itself.
            var under = N(spec.MusicVolume * NarratedUnderSpeechGain);
            graph.Add($"[{musicInput.Value}:a]{stereo},atrim=start={N(spec.MusicStartSeconds)}:duration={N(total)},asetpts=PTS-STARTPTS," +
                      $"volume=volume='if(lt(t\\,{N(speech)})\\,{under}\\,{N(spec.MusicDropVolume ?? spec.MusicVolume)})':eval=frame[music]");
            graph.Add($"{layers}[music]amix=inputs={layerCount + 1}:duration=first:dropout_transition=2:normalize=0,alimiter=limit=0.9[aout]");
        }

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]", "-map", "[aout]", "-t", N(total),
            "-r", fps, "-c:v", "libx264", "-preset", "medium", "-crf", spec.Crf.ToString(invariant)]);
        if (spec.EncoderThreads > 0) args.AddRange(["-threads", spec.EncoderThreads.ToString(invariant)]);
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", spec.OutputPath]);
        return args;
    }

    private static void ValidateNarrated(FfmpegNarratedSpec spec)
    {
        if (spec.Shots.Count is < 2 or > 60) throw new ArgumentException("A narrated clip needs 2-60 shots.", nameof(spec));
        if (spec.Shots.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.InSeconds < 0 || x.DurationSeconds <= 0.05 || !double.IsFinite(x.DurationSeconds)))
            throw new ArgumentOutOfRangeException(nameof(spec), "Every shot needs a file, a start and a length.");
        if (!spec.Shots.Any(x => x.Payoff)) throw new ArgumentException("A narrated clip needs the moment itself.", nameof(spec));
        // Montage first, then the moment: the narration plays over exactly the montage.
        var firstPayoff = spec.Shots.ToList().FindIndex(x => x.Payoff);
        if (spec.Shots.Skip(firstPayoff).Any(x => !x.Payoff))
            throw new ArgumentException("Montage shots must all come before the moment itself.", nameof(spec));
        var montage = spec.Shots.Take(firstPayoff).Sum(x => x.DurationSeconds);
        if (firstPayoff == 0 || Math.Abs(montage - spec.NarrationSeconds) > 0.1)
            throw new ArgumentException($"The montage runs {montage:0.00}s but the narration {spec.NarrationSeconds:0.00}s.", nameof(spec));
        if (spec.NarrationSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(spec), "The narration must run 1-30 seconds.");
        if (spec.FramesPerSecond is < 12 or > 60) throw new ArgumentOutOfRangeException(nameof(spec), "Frame rate must be 12-60.");
        if (spec.Crf is < 0 or > 51) throw new ArgumentOutOfRangeException(nameof(spec), "CRF must be 0-51.");
        if (spec.MusicVolume is < 0 or > 1.5 || spec.MusicDropVolume is < 0 or > 1.5 || spec.VoiceVolume is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(spec), "Music volume is 0-1.5 and voice volume 0-4.");
        if (spec.FreezeSeconds is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(spec), "The ending freeze must be 0-3 seconds.");
        if (spec.Sounds is { } sounds && (sounds.Count > 8 || sounds.Any(x => string.IsNullOrWhiteSpace(x.Path) || !double.IsFinite(x.AtSeconds)
                || x.AtSeconds < 0 || x.Volume is < 0 or > 4)))
            throw new ArgumentOutOfRangeException(nameof(spec), "A clip takes up to 8 sound effects, each with a file, a start and a volume of 0-4.");
        // A cutaway covers the moment only: the narrator's montage has its own shots, and the clip ends on the
        // speaker's own last frame, which the freeze and the skull need.
        var speech = spec.NarrationSeconds + spec.Shots.Where(x => x.Payoff).Sum(x => x.DurationSeconds);
        if (spec.Cutaways is { } cutaways && (cutaways.Count > 8 || cutaways.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.InSeconds < 0 ||
                !double.IsFinite(x.AtSeconds) || !double.IsFinite(x.DurationSeconds) || x.DurationSeconds <= 0.05 ||
                x.AtSeconds < spec.NarrationSeconds || x.AtSeconds + x.DurationSeconds > speech + 0.001)))
            throw new ArgumentOutOfRangeException(nameof(spec), "A clip takes up to 8 cutaways, each a file and a stretch inside the moment.");
    }
}
