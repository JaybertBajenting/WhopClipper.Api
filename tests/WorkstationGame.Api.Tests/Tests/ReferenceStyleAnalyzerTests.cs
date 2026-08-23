using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class ReferenceStyleAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clipper-tests", Guid.NewGuid().ToString("N"));

    public ReferenceStyleAnalyzerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DerivesShotLengthsFromCutTimestamps()
    {
        var shots = ReferenceStyleAnalyzer.SummarizeShots([2, 5, 6], durationSeconds: 10);

        Assert.Equal(3, shots.CutCount);
        Assert.Equal(18, shots.CutsPerMinute);
        Assert.Equal(2.5, shots.AverageShotSeconds);
        Assert.Equal(2.5, shots.MedianShotSeconds);
        Assert.Equal(1, shots.ShortestShotSeconds);
        Assert.Equal(4, shots.LongestShotSeconds);
        Assert.Equal(2, shots.OpeningShotSeconds);
    }

    [Fact]
    public void TreatsAnUncutReferenceAsOneShot()
    {
        var shots = ReferenceStyleAnalyzer.SummarizeShots([], durationSeconds: 42);

        Assert.Equal(0, shots.CutCount);
        Assert.Equal(0, shots.CutsPerMinute);
        Assert.Equal(42, shots.AverageShotSeconds);
        Assert.Equal(42, shots.OpeningShotSeconds);
    }

    [Theory]
    [InlineData(45, "VeryFast")]
    [InlineData(20, "Fast")]
    [InlineData(12, "Medium")]
    [InlineData(3, "Slow")]
    public void ClassifiesPacingFromCutDensity(double cutsPerMinute, string expected) =>
        Assert.Equal(expected, ReferenceStyleAnalyzer.ClassifyPacing(cutsPerMinute));

    [Theory]
    [InlineData(1080, 1920, "9:16", "Vertical")]
    [InlineData(1920, 1080, "16:9", "Horizontal")]
    [InlineData(1080, 1080, "1:1", "Square")]
    [InlineData(1080, 1350, "4:5", "Vertical")]
    public void DescribesFrameGeometry(int width, int height, string ratio, string orientation)
    {
        Assert.Equal(ratio, ReferenceStyleAnalyzer.DescribeAspectRatio(width, height));
        Assert.Equal(orientation, ReferenceStyleAnalyzer.DescribeOrientation(width, height));
    }

    [Fact]
    public void CountsNarrationSpeedFromASubtitleFile()
    {
        var path = Path.Combine(_root, "reference.srt");
        File.WriteAllText(path, """
            1
            00:00:00,000 --> 00:00:02,000
            <i>one two three four</i>

            2
            00:00:02,000 --> 00:00:04,000
            five six

            """);

        Assert.Equal(180, ReferenceStyleAnalyzer.ReadSpokenWordsPerMinute(path, durationSeconds: 2));
        Assert.Null(ReferenceStyleAnalyzer.ReadSpokenWordsPerMinute(null, durationSeconds: 2));
        Assert.Null(ReferenceStyleAnalyzer.ReadSpokenWordsPerMinute(path, durationSeconds: 0));
    }

    [Fact]
    public void SummaryDescribesPacingAndLoudnessInPlainLanguage()
    {
        var profile = new ReferenceStyleProfile(ReferenceStyleAnalyzer.SchemaVersion, 30, 1080, 1920, "9:16",
            "Vertical", 30, HasAudio: true, "Fast", ReferenceStyleAnalyzer.SummarizeShots([2, 5, 6], 30),
            new LoudnessStatistics(-14.2, 5.7, -1.0), 165, [2, 5, 6], [], DateTimeOffset.UtcNow);

        var summary = ReferenceStyleAnalyzer.Describe(profile);

        Assert.Contains(summary, line => line.Contains("Vertical 1080x1920 (9:16)"));
        Assert.Contains(summary, line => line.Contains("Fast pacing"));
        Assert.Contains(summary, line => line.Contains("-14.2 LUFS"));
        Assert.Contains(summary, line => line.Contains("165 words per minute"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
