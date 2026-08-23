using System.Text.Json;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class ProcessContractTests
{
    private const string ValidJson = """
        {
          "highlights": [{
            "startSeconds": 10,
            "endSeconds": 30,
            "hook": "Hook",
            "rationale": "Clear payoff",
            "score": 0.9,
            "confidence": 0.8,
            "editPlan": {
              "framing": "CenterCrop",
              "burnSubtitles": true,
              "subtitleStyle": "Bold",
              "ctaOverlayText": null,
              "musicAssetId": null,
              "musicVolume": 0.15,
              "musicDuckingDb": -12,
              "soundEffects": [],
              "overlays": []
            }
          }]
        }
        """;

    [Fact]
    public void FfmpegArgumentsAreTypedAndDoNotUseAShell()
    {
        var arguments = FfmpegArgumentBuilder.Build("source file.mp4", "output file.mp4", 12.5, 42.5, 1080, 1920, 30, 20, true);
        Assert.Contains("source file.mp4", arguments);
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,fps=30", arguments);
        Assert.Contains("loudnorm=I=-14:TP=-1.5:LRA=11", arguments);
        Assert.DoesNotContain(arguments, x => x.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParsesDirectClaudeStructuredOutputAndCodexJsonl()
    {
        using var payload = JsonDocument.Parse(ValidJson);
        var direct = AiOutputParser.Parse(ValidJson);
        var wrapped = AiOutputParser.Parse(JsonSerializer.Serialize(new { structured_output = payload.RootElement }));
        var codex = AiOutputParser.Parse(string.Join(Environment.NewLine,
            JsonSerializer.Serialize(new { type = "thread.started", thread_id = "abc" }),
            JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new { id = "item_1", type = "agent_message", text = ValidJson }
            }),
            JsonSerializer.Serialize(new { type = "turn.completed" })));
        Assert.Single(direct);
        Assert.Single(wrapped);
        Assert.Single(codex);
        Assert.Equal(10, direct[0].StartSeconds);
    }

    [Fact]
    public void RejectsReversedHighlightRange()
    {
        var json = ValidJson.Replace("\"startSeconds\": 10", "\"startSeconds\": 30")
            .Replace("\"endSeconds\": 30", "\"endSeconds\": 10");
        Assert.Empty(AiOutputParser.Parse(json));
    }

    [Fact]
    public void RejectsEchoedOrSchemaInvalidHighlights()
    {
        var echoed = JsonSerializer.Serialize(new
        {
            type = "item.completed",
            item = new { type = "command_execution", text = ValidJson }
        });
        var invalid = ValidJson.Replace("\"score\": 0.9", "\"score\": 1.5");
        Assert.Empty(AiOutputParser.Parse(echoed));
        Assert.Empty(AiOutputParser.Parse(invalid));
    }

    [Fact]
    public void RichFfmpegGraphAppliesSubtitleMusicDuckingSfxAndOverlaySafely()
    {
        var root = Path.GetTempPath();
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 12, 42, 1080, 1920, 30, 20, true,
            "Fit", Path.Combine(root, "captions.srt"), "Bold", Path.Combine(root, "music.mp3"), .15, -12,
            [new FfmpegSoundEffectInput(Path.Combine(root, "pop.wav"), 1.25, -3)],
            [new FfmpegOverlayInput(Path.Combine(root, "overlay.txt"), 2, 5, "Top", 48, "white")],
            SubtitleTimeBase: SubtitleTimeBase.SourceAbsolute));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("force_original_aspect_ratio=decrease,pad=1080:1920", graph);
        Assert.Contains("subtitles=", graph);
        Assert.Contains("setpts=PTS-STARTPTS+12/TB,subtitles=", graph);
        Assert.Contains("setpts=PTS-12/TB,drawtext=", graph);
        Assert.Contains("drawtext=fontfile=", graph);
        Assert.Contains(":textfile=", graph);
        Assert.Contains("asplit=2[voice][voicekey]", graph);
        Assert.Contains("[musicbase][voicekey]sidechaincompress", graph);
        Assert.Contains("adelay=1250:all=1,volume=-3dB", graph);
        Assert.Contains("amix=inputs=3:duration=first:dropout_transition=2", graph);
        Assert.DoesNotContain("Call me now", graph);
        var musicIndex = arguments.ToList().IndexOf(Path.Combine(root, "music.mp3"));
        Assert.True(musicIndex >= 5);
        Assert.Equal(new[] { "-stream_loop", "-1", "-t", "30", "-i" },
            arguments.Skip(musicIndex - 5).Take(5));
    }

    /// <summary>
    /// Captions built per clip already start at zero. Shifting the frames onto the source clock
    /// for those would ask the subtitles filter for events 12 seconds past the end of a 30 second
    /// clip, and the whole caption track would silently render as nothing.
    /// </summary>
    [Fact]
    public void ClipRelativeCaptionsAreNotShiftedOntoTheSourceClock()
    {
        var root = Path.GetTempPath();
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 12, 42, 1080, 1920, 30, 20, true,
            "CenterCrop", Path.Combine(root, "captions.ass"), null, null, .15, -12, [], [],
            SubtitleTimeBase: SubtitleTimeBase.ClipRelative));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("setpts=PTS-STARTPTS,subtitles=", graph);
        Assert.DoesNotContain("+12/TB", graph);
        Assert.DoesNotContain("setpts=PTS-12/TB", graph);
    }

    [Fact]
    public void BrandingLogoIsTrimmedScaledAndOverlaidOverEverythingElse()
    {
        var root = Path.GetTempPath();
        var logoPath = Path.Combine(root, "logo.png");
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 12, 42, 1080, 1920, 30, 20, true,
            "CenterCrop", Path.Combine(root, "captions.ass"), null, null, .15, -12, [], [],
            Logo: new FfmpegLogoOverlay(logoPath, "Bottom", .45, .14, .9, "716:112:656:890")));
        var list = arguments.ToList();
        var graph = list[list.IndexOf("-filter_complex") + 1];
        Assert.Equal("-i", list[list.IndexOf(logoPath) - 1]);
        Assert.Contains("[1:v]crop=716:112:656:890,scale=486:-1,format=rgba,colorchannelmixer=aa=0.9[vlogo]", graph);
        Assert.Contains("[vbase][vlogo]overlay=(W-w)/2:H-h-H*0.14:format=auto,format=yuv420p,setsar=1[vout]", graph);
        // The mark has to sit over the captions and the hook, not under them.
        Assert.True(graph.IndexOf("subtitles=", StringComparison.Ordinal) < graph.IndexOf("[vlogo]", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 16:9 source squeezed to 9:16 keeps only the middle 31% of the width, so a subject who
    /// sits off-centre gets sliced in half. SmartCrop has to follow them instead.
    /// </summary>
    [Fact]
    public void SmartCropFollowsTheTrackedSubjectInsteadOfCentringTheFrame()
    {
        var root = Path.GetTempPath();
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 30, 1080, 1920, 30, 20, true,
            "SmartCrop", null, null, null, .15, -12, [], [],
            SubjectTrack: new FfmpegSubjectTrack(1920, 1080,
                [new CropKeyframe(0, 656), new CropKeyframe(10, 856)])));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        // 1080 tall * (1080/1920) = 607.5 of source width survives, rounded to an even 608.
        Assert.Contains("crop=608:1080:", graph);
        // Linear ramp from 656 to 856 over the first 10s, clamped inside the frame.
        Assert.Contains("'clip(656+20*clip(t-0\\,0\\,10)\\,0\\,in_w-out_w)'", graph);
        Assert.Contains("scale=1080:1920", graph);
        Assert.DoesNotContain("force_original_aspect_ratio=increase", graph);
    }

    [Fact]
    public void CentreCropIsUnchangedWhenNoSubjectWasTracked()
    {
        var root = Path.GetTempPath();
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 30, 1080, 1920, 30, 20, true,
            "SmartCrop", null, null, null, .15, -12, [], []));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920", graph);
    }

    /// <summary>
    /// An RGBA logo pulls the overlay output up to 4:4:4, which encodes to a stream phones
    /// cannot hardware-decode and platforms re-encode - and doubles what x264 allocates.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlwaysDeliversSquarePixelYuv420(bool withLogo)
    {
        var root = Path.GetTempPath();
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 30, 1080, 1920, 30, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [],
            Logo: withLogo ? new FfmpegLogoOverlay(Path.Combine(root, "logo.png")) : null));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("format=yuv420p,setsar=1[vout]", graph);
    }

    /// <summary>
    /// Measured across sixteen reference shorts: a full-frame green wash of 0.4-0.7s on reaction
    /// beats, and separately timed punch-ins. Both must land on the footage before captions and
    /// branding, so the caption over a flash stays white and upright.
    /// </summary>
    [Fact]
    public void ReactionFlashAndZoomPunchApplyToFootageOnlyAndBeforeCaptions()
    {
        var root = Path.GetTempPath();
        var arguments = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 20, 1080, 1920, 25, 20, true,
            "CenterCrop", Path.Combine(root, "captions.ass"), null, null, .15, -12, [], [],
            ColorFlashes: [new FfmpegColorFlash(3, 0.5, 0.45), new FfmpegColorFlash(9, 0.5, 0.45),
                new FfmpegColorFlash(12, 0.5, 0.45, "Red")],
            ZoomPunches: [new FfmpegZoomPunch(6, 0.6, 1.3)]));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        // One mixer per colour; a single filter cannot be two colours at different moments.
        Assert.Contains("colorchannelmixer=rr=0.55:gg=1.45:bb=0.55:enable='between(t\\,3\\,3.5)+between(t\\,9\\,9.5)'", graph);
        Assert.Contains("colorchannelmixer=rr=1.45:gg=0.55:bb=0.55:enable='between(t\\,12\\,12.5)'", graph);
        Assert.Contains("zoompan=z='if(between(in_time\\,6\\,6+0.6)\\,1+0.3*(in_time-6)/0.6\\,1)'", graph);
        // Footage effects first, captions on top of them.
        Assert.True(graph.IndexOf("zoompan", StringComparison.Ordinal) < graph.IndexOf("subtitles=", StringComparison.Ordinal));
        Assert.True(graph.IndexOf("colorchannelmixer", StringComparison.Ordinal) < graph.IndexOf("subtitles=", StringComparison.Ordinal));
    }

    /// <summary>
    /// The CTA overlay is always bottom-anchored and so is the branding logo, so before this
    /// they were drawn on top of each other on every campaign that had a logo.
    /// </summary>
    [Fact]
    public void HookCardsAreCompositedAsImagesUnderTheBrandingLogo()
    {
        var root = Path.GetTempPath();
        // A composited clip carries the facecam across the top, so the hook has to be pinned
        // below it by fraction. Drawn at the top it lands on the streamer's face.
        var card = new FfmpegOverlayInput(Path.Combine(root, "hook.txt"), 0, 3.2, "Top", 48, "white",
            CardImagePath: Path.Combine(root, "hook.png"), YFraction: 0.3156);
        var logo = new FfmpegLogoOverlay(Path.Combine(root, "logo.png"), "Bottom", .45, .14, 1, null);

        var args = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 14, 1080, 1920, 25, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [card], Logo: logo));
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];

        // Composited from the PNG, not drawn: drawtext cannot round a box's corners.
        Assert.DoesNotContain("drawtext", graph);
        Assert.Contains(Path.Combine(root, "hook.png"), args);
        Assert.Contains("overlay=(W-w)/2:H*0.3156", graph);
        Assert.Contains("enable='between(t,0,3.2)'", graph);
        // Branding still goes on last, over the card.
        Assert.True(graph.IndexOf("[vlogo]", StringComparison.Ordinal) >
                    graph.IndexOf("[card0]", StringComparison.Ordinal));
        Assert.EndsWith("format=yuv420p,setsar=1[vout]", graph.Split(';').First(x => x.Contains("[vout]")));
    }

    [Fact]
    public void OutlineOverlaysAreStillDrawnAsTextWhenNoCardIsSupplied()
    {
        var root = Path.GetTempPath();
        var overlay = new FfmpegOverlayInput(Path.Combine(root, "hook.txt"), 0, 3, "Top", 48, "white");

        var args = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 14, 1080, 1920, 25, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [overlay]));
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("drawtext", graph);
        Assert.Contains("y=h*0.12-text_h/2", graph);
    }

    [Fact]
    public void BottomTextIsLiftedClearOfTheBrandingLogo()
    {
        var root = Path.GetTempPath();
        var overlay = new FfmpegOverlayInput(Path.Combine(root, "cta.txt"), 8, 12, "Bottom", 48, "white");
        var logo = new FfmpegLogoOverlay(Path.Combine(root, "logo.png"), "Bottom", .45, .14, 1, "722:120:652:888");

        var withLogo = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 14, 1080, 1920, 25, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [overlay], Logo: logo));
        var withoutLogo = FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 14, 1080, 1920, 25, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [overlay]));

        var lifted = withLogo[withLogo.ToList().IndexOf("-filter_complex") + 1];
        var plain = withoutLogo[withoutLogo.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("y=h*0.85-text_h/2", plain);
        // 1080 * 0.45 = 486 wide; the 722x120 artwork makes that 81 tall.
        Assert.Contains("y=h-h*0.14-81-text_h-24", lifted);
        Assert.DoesNotContain("y=h*0.85-text_h/2", lifted);
    }

    [Fact]
    public void RejectsAZoomPunchScaledBeyondWhatTheFrameCanCover()
    {
        var root = Path.GetTempPath();
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 20, 1080, 1920, 25, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [],
            ZoomPunches: [new FfmpegZoomPunch(3, 0.5, 9)])));
    }

    [Fact]
    public void BrandingLogoRejectsAnUnsafeCropRectangle()
    {
        var root = Path.GetTempPath();
        Assert.Throws<ArgumentException>(() => FfmpegArgumentBuilder.Build(new FfmpegRenderSpec(
            Path.Combine(root, "source.mp4"), Path.Combine(root, "output.mp4"), 0, 30, 1080, 1920, 30, 20, true,
            "CenterCrop", null, null, null, .15, -12, [], [],
            Logo: new FfmpegLogoOverlay(Path.Combine(root, "logo.png"), CropRect: "10:10:0:0[x]v]scale=2"))));
    }

    [Fact]
    public async Task CliInvocationsUseIsolatedNonInteractiveFlags()
    {
        var codexRunner = new CapturingRunner(JsonSerializer.Serialize(new
        {
            type = "item.completed", item = new { type = "agent_message", text = ValidJson }
        }));
        var temp = Path.Combine(Path.GetTempPath(), "whop-clipper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var provider = new CodexCliProvider(codexRunner, Options.Create(new AppOptions { CodexPath = "codex" }));
            await provider.AnalyzeAsync(new AiProviderRequest("prompt", Path.Combine(temp, "schema.json"), temp), default);
            var arguments = codexRunner.Invocation!.Arguments;
            Assert.Contains("--ephemeral", arguments);
            Assert.Contains("--sandbox", arguments);
            Assert.Contains("read-only", arguments);
            Assert.Contains("--ignore-user-config", arguments);
            Assert.Contains("--ignore-rules", arguments);
            Assert.Contains("--skip-git-repo-check", arguments);
            Assert.Contains("--output-last-message", arguments);

            using var payload = JsonDocument.Parse(ValidJson);
            var claudeRunner = new CapturingRunner(JsonSerializer.Serialize(new { structured_output = payload.RootElement }));
            var claude = new ClaudeCliProvider(claudeRunner, Options.Create(new AppOptions { ClaudePath = "claude" }));
            await claude.AnalyzeAsync(new AiProviderRequest("prompt", Path.Combine(temp, "schema.json"), temp), default);
            Assert.Contains("--safe-mode", claudeRunner.Invocation!.Arguments);
            Assert.Contains("--no-session-persistence", claudeRunner.Invocation.Arguments);
            Assert.Contains("plan", claudeRunner.Invocation.Arguments);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private sealed class CapturingRunner(string output) : IProcessRunner
    {
        public ProcessInvocation? Invocation { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            Invocation = invocation;
            return Task.FromResult(new ProcessResult(0, output, string.Empty, false, TimeSpan.Zero));
        }
    }
}
