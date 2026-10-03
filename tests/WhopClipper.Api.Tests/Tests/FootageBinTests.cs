using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests.Tests;

public sealed class FootageBinTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "footage-bin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AWideVideoKeepsItsHeightAndSlidesTheWindowAcross()
    {
        // 1920x1080 into the 1080x960 panel: 1214 px of width (even), full height.
        Assert.Equal(new CropRectDto(0, 0, 1214, 1080), FootageBin.Crop(1920, 1080, 0));
        Assert.Equal(new CropRectDto(353, 0, 1214, 1080), FootageBin.Crop(1920, 1080, 0.5));
        Assert.Equal(new CropRectDto(706, 0, 1214, 1080), FootageBin.Crop(1920, 1080, 1));
        // Out-of-range framing is held to the frame.
        Assert.Equal(new CropRectDto(706, 0, 1214, 1080), FootageBin.Crop(1920, 1080, 7));
    }

    [Fact]
    public void ATallVideoKeepsItsWidthAndIsCentred()
    {
        Assert.Equal(new CropRectDto(0, 480, 1080, 960), FootageBin.Crop(1080, 1920, 0.2));
    }

    [Fact]
    public void TheBinListsWhatWasAddedAndNeverReachesOutsideItself()
    {
        var first = new FootageItem(Guid.NewGuid(), "https://youtu.be/a", "Logan Paul vlog", 10, 70, FootageBin.Ready,
            "a.mp4", 60, 1920, 1080, null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = first with { Id = Guid.NewGuid(), Title = "Mike Majlak podcast", Status = FootageBin.Downloading,
            FileName = null, CreatedAt = DateTimeOffset.UtcNow };
        FootageBin.Write(_folder, first);
        FootageBin.Write(_folder, second);
        File.WriteAllBytes(Path.Combine(_folder, "a.mp4"), [1, 2, 3]);

        Assert.Equal(["Logan Paul vlog", "Mike Majlak podcast"], FootageBin.List(_folder).Select(x => x.Title));
        Assert.Equal(Path.Combine(_folder, "a.mp4"), FootageBin.FilePath(_folder, first));
        Assert.Null(FootageBin.FilePath(_folder, second));
        // A record naming a path outside the bin is reduced to a bare file name inside it.
        Assert.Null(FootageBin.FilePath(_folder, first with { FileName = @"..\..\secret.mp4" }));

        var removed = new List<string>();
        FootageBin.Remove(_folder, first, path => { removed.Add(Path.GetFileName(path)); File.Delete(path); });
        Assert.Equal(["a.mp4", first.Id.ToString("N") + ".json"], removed);
        Assert.Single(FootageBin.List(_folder));
    }

    [Fact]
    public void EveryWayOfWritingAYouTubeLinkIsTheSameVideo()
    {
        var key = FootageBin.VideoKey("https://www.youtube.com/watch?v=8m-4hF_AB_c");
        Assert.Equal("youtube:8m-4hF_AB_c", key);
        Assert.Equal(key, FootageBin.VideoKey("https://youtu.be/8m-4hF_AB_c?si=abc&t=286"));
        Assert.Equal(key, FootageBin.VideoKey("https://m.youtube.com/watch?v=8m-4hF_AB_c&t=4m46s"));
        Assert.Equal(key, FootageBin.VideoKey("  https://www.youtube.com/shorts/8m-4hF_AB_c "));
        Assert.Equal("8m-4hF_AB_c", FootageBin.YouTubeId("https://youtu.be/8m-4hF_AB_c"));
        Assert.NotEqual(key, FootageBin.VideoKey("https://www.youtube.com/watch?v=mYC5DX8LoF8"));
        Assert.Null(FootageBin.YouTubeId("https://vimeo.com/12345"));
    }

    [Fact]
    public void AStretchAlreadyDownloadedIsHandedBackWithWhereItStarts()
    {
        var steve = new FootageItem(Guid.NewGuid(), "https://www.youtube.com/watch?v=8m-4hF_AB_c", "SteveWillDoIt EP 483", 280, 340,
            FootageBin.Ready, "s.mp4", 60.02, 1920, 1080, null, DateTimeOffset.UtcNow, SourceDurationSeconds: 5400);
        var items = new[] { steve };

        // Inside it, however the link is written: no second download, and the in-point is where 286 s falls.
        var held = FootageBin.FindCovering(items, "https://youtu.be/8m-4hF_AB_c?t=286", 286, 289);
        Assert.Equal((steve.Id, 6d), (held!.Value.Item.Id, held.Value.InSeconds));
        // Running past its end, starting before it, the whole video, or another video: a new download.
        Assert.Null(FootageBin.FindCovering(items, steve.Url, 330, 360));
        Assert.Null(FootageBin.FindCovering(items, steve.Url, 270, 290));
        Assert.Null(FootageBin.FindCovering(items, steve.Url, null, null));
        Assert.Null(FootageBin.FindCovering(items, "https://www.youtube.com/watch?v=mYC5DX8LoF8", 286, 289));
        // A failed download holds nothing.
        Assert.Null(FootageBin.FindCovering([steve with { Status = FootageBin.Failed }], steve.Url, 286, 289));
    }

    [Fact]
    public void ADownloadStillRunningCountsSoPastingTwiceStartsOneCopy()
    {
        var whole = new FootageItem(Guid.NewGuid(), "https://youtu.be/abc", "abc", null, null, FootageBin.Downloading, null, 0, 0, 0,
            null, DateTimeOffset.UtcNow);
        Assert.Equal(whole.Id, FootageBin.FindCovering([whole], "https://www.youtube.com/watch?v=abc", null, null)!.Value.Item.Id);
        Assert.Equal(125d, FootageBin.FindCovering([whole], "https://youtu.be/abc", 125, 130)!.Value.InSeconds);
        // Asked for up to 9:00 of a 7:58 video: anything that reaches the end holds it.
        var tail = whole with { Status = FootageBin.Ready, FromSeconds = 400, ToSeconds = 540, DurationSeconds = 78,
            SourceDurationSeconds = 478 };
        Assert.Equal(20d, FootageBin.FindCovering([tail], tail.Url, 420, 540)!.Value.InSeconds);
    }

    [Fact]
    public void FootageKeptPerClipMovesIntoTheCampaignsSharedFootage()
    {
        var clip = Guid.NewGuid();
        var old = Path.Combine(_folder, clip.ToString("N"));
        var item = new FootageItem(Guid.NewGuid(), "https://youtu.be/a", "Impaulsive EP 181", 300, 330, FootageBin.Ready,
            "a1.mp4", 30.067, 1920, 1080, null, DateTimeOffset.UtcNow);
        FootageBin.Write(old, item);
        File.WriteAllBytes(Path.Combine(old, "a1.mp4"), [1, 2, 3]);

        var listed = Assert.Single(FootageBin.List(_folder));
        Assert.Equal((item.Id, clip), (listed.Id, listed.AddedForPlanId));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(FootageBin.FilePath(_folder, listed)!));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void FfmpegsProgressReportGivesSecondsWrittenAndBytes()
    {
        var seen = new List<(double Seconds, long? Bytes)>();
        var reader = new FfmpegProgressReader((seconds, bytes) => seen.Add((seconds, bytes)));
        // Captured from yt-dlp fetching a section with "--downloader-args ffmpeg:-progress pipe:2", fed in pieces
        // that split lines, the way the pipe delivers them.
        const string report = "frame=1\ntotal_size=44\nout_time_us=-1300000\nout_time=-00:00:01.300000\nprogress=continue\n" +
            "frame=163\ntotal_size=1835052\nout_time_us=4100000\nout_time=00:00:04.100000\nspeed=3.97x\nprogress=continue\n" +
            "frame=424\ntotal_size=5242924\nout_time_us=12799000\nprogress=end\n";
        foreach (var piece in report.Chunk(37)) reader.Feed(new string(piece));

        Assert.Equal([(0d, 44L), (4.1, 1835052L), (12.799, 5242924L)], seen.Select(x => (x.Seconds, x.Bytes)));
        Assert.Equal("ERROR: unable to download video data: HTTP Error 403",
            FfmpegProgressReader.WithoutReports(report + "ERROR: unable to download video data: HTTP Error 403\n"));
    }

    [Fact]
    public void ShotTimesRoundedToTheMillisecondSelectOnlyTheShotsMeant()
    {
        // The Mike's Bikes clip: shots of 2.2325 s, which the editor shows as starting at 0, 2.232, 4.465.
        var shots = new[] { 2.2325, 2.2325, 1.8875, 2.578 }.Select((d, i) => new BrollShotDto($"s{i}.mp4", 0, d,
            new CropRectDto(0, 0, 10, 10), 1920, 1080, [], "q")).ToList();
        var picked = BrollRepairSupport.Select(shots, 2.232, 6.352, BrollRepairSupport.EditorMaxSeconds);
        Assert.Equal((1, 3), (picked.FirstShot, picked.LastShotExclusive));
        Assert.Equal(4.12, picked.DurationSeconds, 2);
    }

    [Fact]
    public void TheEditorMayReplaceLongerRunsThanAnAiRepair()
    {
        var shots = Enumerable.Range(0, 10).Select(i => new BrollShotDto($"s{i}.mp4", 0, 4, new CropRectDto(0, 0, 10, 10),
            1920, 1080, [], "q")).ToList();
        Assert.Throws<ArgumentOutOfRangeException>(() => BrollRepairSupport.Select(shots, 0, 40));
        var whole = BrollRepairSupport.Select(shots, 0, 40, BrollRepairSupport.EditorMaxSeconds);
        Assert.Equal((0d, 40d, 0, 10), (whole.StartSeconds, whole.EndSeconds, whole.FirstShot, whole.LastShotExclusive));
    }
}
