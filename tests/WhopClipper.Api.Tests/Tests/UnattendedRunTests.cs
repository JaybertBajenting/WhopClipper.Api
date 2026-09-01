using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// Guards the one thing that separates <see cref="ClipStylist.BuildForWindowAsync"/> from
/// <see cref="ClipStylist.BuildAsync"/>: it must not re-choose the window.
///
/// Clips whose in and out points were picked by reading the transcript have to keep them. Routing
/// them through the window heuristic clamps them to 12-19.5s, which would cut the payoff out of
/// the longer ones - a 32 second clip would lose half of itself. Nothing else in the suite would
/// catch that, and the damage only shows up in a finished render.
/// </summary>
public sealed class ClipStylistWindowTests
{
    /// <summary>
    /// The stylist asks ffmpeg for its window as `-ss start -t duration`, so the captured
    /// invocation says exactly which window it decided on - no ffmpeg, no wav file needed.
    /// Returning a failure exit makes the stylist stop right after that call.
    /// </summary>
    private sealed class WindowSpy : IProcessRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            Arguments ??= invocation.Arguments;
            return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty, false, TimeSpan.Zero));
        }
    }

    private static (ClipStylist Stylist, WindowSpy Spy) Build()
    {
        var spy = new WindowSpy();
        var options = Options.Create(new AppOptions { FfmpegPath = "ffmpeg" });
        return (new ClipStylist(spy, options, NullLogger<ClipStylist>.Instance), spy);
    }

    /// <summary>
    /// 32 seconds of speech with exactly one usable ending, at 15.9s: a sentence-final word
    /// followed by a 0.6s breath. Everywhere else the words are 0.1s apart, which is too tight to
    /// count as a boundary. So the heuristic has one place it can cut, and it is far short of the
    /// full window - which is the situation a hand-picked 32 second clip is actually in.
    /// </summary>
    private static TranscriptWord[] SpeechWithOneEndingAt15() =>
        Enumerable.Range(0, 32)
            .Select(i => i switch
            {
                15 => new TranscriptWord(15, 15.9, "over."),      // sentence end
                16 => new TranscriptWord(16.5, 17.4, "then"),     // 0.6s gap before it
                _ => new TranscriptWord(i, i + 0.9, $"word{i}"),
            })
            .ToArray();

    private static (double Start, double Duration) WindowAskedFor(IReadOnlyList<string> arguments)
    {
        var list = arguments.ToList();
        return (double.Parse(list[list.IndexOf("-ss") + 1]), double.Parse(list[list.IndexOf("-t") + 1]));
    }

    /// <summary>The whole reason the method exists: a deliberate 32 second cut stays 32 seconds.</summary>
    [Fact]
    public async Task BuildForWindowKeepsALongHandPickedCutIntact()
    {
        var (stylist, spy) = Build();

        await stylist.BuildForWindowAsync("clip.mp4", 0, 32, SpeechWithOneEndingAt15(), [], 0, CancellationToken.None);

        var (start, duration) = WindowAskedFor(spy.Arguments!);
        Assert.Equal(0, start, 3);
        Assert.Equal(32, duration, 3);
    }

    /// <summary>
    /// The contrast, on the same fixture: the heuristic cuts at its one available ending, losing
    /// half the clip. If this ever matches the window asked for, the two paths have been merged
    /// and hand-picked cuts are silently being re-cut again.
    /// </summary>
    [Fact]
    public async Task BuildAsyncCutsTheSameClipShortAtItsOneEnding()
    {
        var (stylist, spy) = Build();

        await stylist.BuildAsync("clip.mp4", 0, 32, SpeechWithOneEndingAt15(), [], 0, CancellationToken.None);

        var (_, duration) = WindowAskedFor(spy.Arguments!);
        Assert.True(duration is > 15 and < 20,
            $"expected the heuristic to end near its 15.9s boundary, got {duration}s");
    }
}

public sealed class DiskSpaceGuardTests
{
    private static DiskSpaceGuard Guard(double headroom = 2.5, long floorBytes = 5L * 1024 * 1024 * 1024)
    {
        var options = Options.Create(new AppOptions
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "whopclipper-tests"),
            PreferredVideoQuality = "Best",
            DiskHeadroomFactor = headroom,
            MinimumFreeDiskBytes = floorBytes
        });
        return new DiskSpaceGuard(new WorkspaceService(options), options);
    }

    /// <summary>
    /// A 3.4 hour VOD really did land at 2.29 GB, and the estimate has to sit above that, because
    /// the download, the merge and the rendered clips are all on disk together for a moment.
    /// </summary>
    [Fact]
    public void EstimatesAboveWhatARealVodActuallyTook()
    {
        var estimate = Guard().Estimate(3.43 * 60 * 60, "720p");

        Assert.True(estimate > 2.29 * 1024 * 1024 * 1024,
            $"estimated {estimate / 1024d / 1024d / 1024d:0.00} GB for a VOD that took 2.29 GB");
    }

    /// <summary>
    /// An unknown duration is the dangerous case. Treating it as small would wave through exactly
    /// the source most likely to fill the volume, which is what the guard exists to stop.
    /// </summary>
    [Fact]
    public void TreatsAnUnknownDurationAsALongVideo()
    {
        var guard = Guard();

        Assert.True(guard.Estimate(null, "720p") >= guard.Estimate(60 * 60, "720p"));
        Assert.True(guard.Estimate(0, "720p") >= guard.Estimate(60 * 60, "720p"));
    }

    /// <summary>Audio-only runs are a fraction of the cost and must not be blocked like a VOD.</summary>
    [Fact]
    public void ChargesAudioOnlyFarLessThanVideo()
    {
        var guard = Guard();

        Assert.True(guard.Estimate(3600, "audio") * 5 < guard.Estimate(3600, "1080p"));
    }

    /// <summary>The reserve is the point: a run must not be allowed to consume the last byte.</summary>
    [Fact]
    public void RefusesWhenTheSourceWouldEatIntoTheReserve()
    {
        // A floor larger than any real disk, so the check must fail whatever the machine has.
        var guard = Guard(floorBytes: long.MaxValue / 4);

        var error = Assert.Throws<InvalidOperationException>(() => guard.EnsureRoomFor(600, "720p"));

        Assert.Contains("Not enough disk", error.Message);
        Assert.Contains("GB", error.Message);
    }

    /// <summary>A short clip on a machine with room must not be blocked.</summary>
    [Fact]
    public void AllowsASmallSourceWhenThereIsPlentyOfRoom()
    {
        var guard = Guard(headroom: 1, floorBytes: 0);

        guard.EnsureRoomFor(10, "audio");
    }
}

public sealed class TransientAiFailureTests
{
    private static ProcessResult Failed(string output) =>
        new(1, output, string.Empty, false, TimeSpan.Zero);

    /// <summary>
    /// The exact shape the Claude CLI produced when an overnight run lost a source, after the
    /// call had already been charged.
    /// </summary>
    [Fact]
    public void TreatsAClaudeRateLimitAsWorthRetrying()
    {
        var payload = "{\"is_error\":true,\"subtype\":\"success\",\"api_error_status\":429}";

        Assert.True(CliAiProvider.IsTransient(Failed(payload)));
    }

    [Theory]
    [InlineData("Error: 529 overloaded_error")]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("rate_limit_error: please retry")]
    public void TreatsOverloadAndRateLimitWordingAsWorthRetrying(string output) =>
        Assert.True(CliAiProvider.IsTransient(Failed(output)));

    /// <summary>
    /// These repeat identically and cost money each time, so retrying them is worse than failing.
    /// </summary>
    [Theory]
    [InlineData("returned no schema-valid highlights")]
    [InlineData("Invalid API key. Please run /login")]
    [InlineData("--json-schema is not supported")]
    public void DoesNotRetryFailuresThatWillRepeat(string output) =>
        Assert.False(CliAiProvider.IsTransient(Failed(output)));

    /// <summary>A success is never a retry, whatever words happen to appear in its output.</summary>
    [Fact]
    public void NeverRetriesASuccessfulRun()
    {
        var succeeded = new ProcessResult(0, "mentions a rate limit in passing", string.Empty, false, TimeSpan.Zero);

        Assert.False(CliAiProvider.IsTransient(succeeded));
    }
}
