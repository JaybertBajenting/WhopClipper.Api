using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class DownloadedFileResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clipper-tests", Guid.NewGuid().ToString("N"));

    public DownloadedFileResolverTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void PrefersThePathYtDlpReported()
    {
        var reported = Write("reported.mp4", 5_000);
        Write("other.mp4", 9_000);

        var resolved = JobExecutionService.ResolveDownloadedFile($"[download] 100%\n{reported}", _directory, Started());

        Assert.Equal(reported, resolved);
    }

    /// <summary>
    /// A title containing emoji used to come back mangled through the console codepage, which
    /// discarded a finished download. The folder search has to recover it.
    /// </summary>
    [Fact]
    public void FallsBackToTheFolderWhenTheReportedPathIsUnusable()
    {
        var actual = Write("\U0001F534MASSIVE ONE PIECE CARD OPENING\U0001F534-0xkq-BbzKrw.mp4", 600_000);

        var resolved = JobExecutionService.ResolveDownloadedFile("?MASSIVE ONE PIECE?-0xkq-BbzKrw.mp4", _directory, Started());

        Assert.Equal(actual, resolved);
    }

    [Fact]
    public void FallsBackWhenNothingWasReportedAtAll()
    {
        var actual = Write("video.mp4", 400_000);

        Assert.Equal(actual, JobExecutionService.ResolveDownloadedFile(string.Empty, _directory, Started()));
    }

    [Fact]
    public void PicksTheLargestCandidateSoAStraySubtitleOrAudioStubLoses()
    {
        Write("clip.m4a", 2_000);
        var video = Write("clip.mp4", 900_000);

        Assert.Equal(video, JobExecutionService.ResolveDownloadedFile(string.Empty, _directory, Started()));
    }

    [Fact]
    public void IgnoresFilesThatPredateTheJob()
    {
        var stale = Write("old.mp4", 100_000);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        Assert.Null(JobExecutionService.ResolveDownloadedFile(string.Empty, _directory, DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void IgnoresPartialDownloadsAndSubtitles()
    {
        Write("clip.mp4.part", 500_000);
        Write("clip.en.srt", 90_000);

        Assert.Null(JobExecutionService.ResolveDownloadedFile(string.Empty, _directory, Started()));
    }

    private static DateTime Started() => DateTime.UtcNow.AddMinutes(-5);

    private string Write(string name, int bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
