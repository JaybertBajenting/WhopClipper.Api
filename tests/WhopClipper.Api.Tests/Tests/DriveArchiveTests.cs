using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests.Tests;

public sealed class DriveArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "drive-archive-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _clip;

    public DriveArchiveTests()
    {
        _drive = Path.Combine(_root, "drive");
        var output = Path.Combine(_root, "workspace", "campaigns", "c", "output", "tiktok");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(_drive);
        _clip = Path.Combine(output, "Mike Majlak's petition 😂-abc.mp4");
        File.WriteAllBytes(_clip, RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(DriveArchive.PreviewPath(_clip), RandomNumberGenerator.GetBytes(2048));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for rclone (copyto, lsjson --stat) against a local folder, and for ffmpeg's poster.</summary>
    private sealed class FakeTools(string drive) : IProcessRunner
    {
        public bool CorruptUploads { get; set; }
        public string? FailWith { get; set; }
        public List<string> Calls { get; } = [];

        private string Local(string remote) => Path.Combine(drive, remote.Replace("gdrive:", "").Replace('/', Path.DirectorySeparatorChar));

        public Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            var args = invocation.Arguments;
            Calls.Add(args[0]);
            if (invocation.Executable == "ffmpeg-fake")
            {
                File.WriteAllBytes(args[^1], [0xFF, 0xD8, 0xFF]);
                return Ok("");
            }
            if (FailWith is { } error) return Task.FromResult(new ProcessResult(1, "", error, false, TimeSpan.Zero));
            switch (args[0])
            {
                case "copyto" when args[1].StartsWith("gdrive:"):
                    File.Copy(Local(args[1]), args[2], overwrite: true);
                    return Ok("");
                case "copyto":
                    var target = Local(args[2]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(args[1], target, overwrite: true);
                    if (CorruptUploads) File.AppendAllText(target, "x");
                    return Ok("");
                case "lsjson" when args.Contains("--files-only"):
                    var folder = Local(args[^1]);
                    if (!Directory.Exists(folder))
                        return Task.FromResult(new ProcessResult(3, "[\n", "2026/10/02 NOTICE: gdrive: shared client_id\n" +
                            "2026/10/02 ERROR : error listing: directory not found\n" +
                            "2026/10/02 NOTICE: Failed to lsjson with 2 errors: last error was: error in ListJSON: directory not found\n",
                            false, TimeSpan.Zero));
                    return Ok(JsonSerializer.Serialize(Directory.GetFiles(folder).Select(x => new
                    {
                        Path = Path.GetFileName(x), Name = Path.GetFileName(x), Size = new FileInfo(x).Length,
                        Hashes = new { md5 = Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(x))) },
                    })));
                case "lsjson":
                    var file = Local(args[^1]);
                    var md5 = Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(file)));
                    return Ok(JsonSerializer.Serialize(new { Path = Path.GetFileName(file), Name = Path.GetFileName(file),
                        Size = new FileInfo(file).Length, Hashes = new { md5 } }));
                default:
                    throw new InvalidOperationException("unexpected " + args[0]);
            }
        }

        private static Task<ProcessResult> Ok(string output) => Task.FromResult(new ProcessResult(0, output, "", false, TimeSpan.Zero));
    }

    private DriveArchive Archive(FakeTools tools)
    {
        var options = Options.Create(new AppOptions
        {
            WorkspaceRoot = Path.Combine(_root, "workspace"), RclonePath = "rclone-fake", FfmpegPath = "ffmpeg-fake",
            DriveRemote = "gdrive:WhopClipper",
        });
        return new DriveArchive(tools, options, new WorkspaceService(options), NullLogger<DriveArchive>.Instance);
    }

    [Fact]
    public async Task AMovedClipLeavesItsRecordAndThumbnailAndComesBackByteForByte()
    {
        var original = File.ReadAllBytes(_clip);
        var tools = new FakeTools(_drive);
        var archive = Archive(tools);

        var moved = await archive.MoveOutAsync("Double Coverage Podcast Clips", _clip, CancellationToken.None);

        Assert.Equal("gdrive:WhopClipper/Double Coverage Podcast Clips", moved.Remote);
        Assert.Equal(2, moved.Files.Count);
        Assert.Equal(4096 + 2048, moved.Bytes);
        Assert.False(File.Exists(_clip));
        Assert.False(File.Exists(DriveArchive.PreviewPath(_clip)));
        Assert.True(File.Exists(DriveArchive.PosterPath(_clip)));
        Assert.Equal(moved.Remote, DriveArchive.Read(_clip)!.Remote);
        Assert.True(File.Exists(Path.Combine(_drive, "WhopClipper", "Double Coverage Podcast Clips", Path.GetFileName(_clip))));

        await archive.BringBackAsync(_clip, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(_clip));
        Assert.True(File.Exists(DriveArchive.PreviewPath(_clip)));
        Assert.Null(DriveArchive.Read(_clip));
        Assert.False(File.Exists(DriveArchive.PosterPath(_clip)));
        // The copy in Drive is left alone: deleting it is the user's decision.
        Assert.True(File.Exists(Path.Combine(_drive, "WhopClipper", "Double Coverage Podcast Clips", Path.GetFileName(_clip))));
    }

    [Fact]
    public async Task AnUploadThatDoesNotMatchLeavesEverythingOnThisPc()
    {
        var archive = Archive(new FakeTools(_drive) { CorruptUploads = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            archive.MoveOutAsync("Double Coverage Podcast Clips", _clip, CancellationToken.None));

        Assert.Contains("nothing on this PC was removed", error.Message);
        Assert.True(File.Exists(_clip));
        Assert.True(File.Exists(DriveArchive.PreviewPath(_clip)));
        Assert.Null(DriveArchive.Read(_clip));
    }

    [Fact]
    public async Task AnRcloneFailureNamesItsReasonNotTheSharedClientNotice()
    {
        var archive = Archive(new FakeTools(_drive)
        {
            FailWith = "2026/09/29 NOTICE: gdrive: This remote uses rclone's shared Google Drive client_id\n" +
                       "2026/09/29 ERROR : Failed to create file system: didn't find section in config file (\"gdrive\")\n",
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            archive.MoveOutAsync("Double Coverage Podcast Clips", _clip, CancellationToken.None));

        Assert.Contains("didn't find section in config file", error.Message);
        Assert.DoesNotContain("NOTICE", error.Message);
        Assert.True(File.Exists(_clip));
    }

    [Fact]
    public async Task ABringBackInterruptedPartWayIsFinishedByTryingAgain()
    {
        var tools = new FakeTools(_drive);
        var archive = Archive(tools);
        await archive.MoveOutAsync("Double Coverage Podcast Clips", _clip, CancellationToken.None);
        // A half-written file from a download that stopped.
        File.WriteAllBytes(_clip, [1, 2, 3]);

        await archive.BringBackAsync(_clip, CancellationToken.None);

        Assert.Equal(4096, new FileInfo(_clip).Length);
        Assert.Null(DriveArchive.Read(_clip));
    }

    [Fact]
    public async Task UploadingKeepsTheFileHereAndSkipsWhatDriveAlreadyHolds()
    {
        var original = File.ReadAllBytes(_clip);
        var tools = new FakeTools(_drive);
        var archive = Archive(tools);
        var remote = archive.RemoteFor("TJR Trades Clips");
        Assert.Equal("gdrive:WhopClipper/TJR Trades Clips", remote);
        // A campaign never uploaded before has no folder yet; that is "nothing there", not a failure.
        Assert.Empty(await archive.ListAsync(remote, CancellationToken.None));

        var first = await archive.UploadAsync(remote, _clip, null, CancellationToken.None);

        Assert.False(first.AlreadyThere);
        Assert.Equal(4096, first.Bytes);
        // Still on this PC, with no move record: an upload is a backup, not a move.
        Assert.Equal(original, File.ReadAllBytes(_clip));
        Assert.Null(DriveArchive.Read(_clip));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_drive, "WhopClipper", "TJR Trades Clips", Path.GetFileName(_clip))));

        // The second run reads the folder once and sends nothing.
        var listing = await archive.ListAsync(remote, CancellationToken.None);
        tools.Calls.Clear();
        var second = await archive.UploadAsync(remote, _clip, listing.GetValueOrDefault(Path.GetFileName(_clip)), CancellationToken.None);
        Assert.True(second.AlreadyThere);
        Assert.DoesNotContain("copyto", tools.Calls);

        // A clip re-rendered since is a different file under the same name, so it is sent again.
        File.WriteAllBytes(_clip, RandomNumberGenerator.GetBytes(5000));
        var third = await archive.UploadAsync(remote, _clip, listing.GetValueOrDefault(Path.GetFileName(_clip)), CancellationToken.None);
        Assert.False(third.AlreadyThere);
        Assert.Equal(5000, new FileInfo(Path.Combine(_drive, "WhopClipper", "TJR Trades Clips", Path.GetFileName(_clip))).Length);
    }

    [Fact]
    public async Task AnUploadThatArrivesDamagedIsReportedAndTheFileStays()
    {
        var archive = Archive(new FakeTools(_drive) { CorruptUploads = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            archive.UploadAsync(archive.RemoteFor("TJR Trades Clips"), _clip, null, CancellationToken.None));

        Assert.Contains("did not arrive in Google Drive intact", error.Message);
        Assert.True(File.Exists(_clip));
    }

    [Fact]
    public void TheCampaignsSourceVideosAndFinishedClipsAreWhatGetsUploaded()
    {
        var campaign = Path.Combine(_root, "workspace", "campaigns", "c");
        var old = DateTime.UtcNow.AddMinutes(-10);
        string Make(string relative, int bytes = 100, DateTime? written = null)
        {
            var path = Path.Combine(campaign, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, written ?? old);
            return path;
        }
        File.SetLastWriteTimeUtc(_clip, old);
        File.SetLastWriteTimeUtc(DriveArchive.PreviewPath(_clip), old);
        var episode = Make(Path.Combine("source", "episode [abc123].mp4"), 900);
        Make(Path.Combine("source", "episode [abc123].mp4.part"), 50);      // a download still coming in
        Make(Path.Combine("source", "episode [abc123].info.json"), 10);
        Make(Path.Combine("output", "tiktok", "rendering now.mp4"), 300, DateTime.UtcNow);
        Make(Path.Combine("output", "tiktok", "empty.mp4"), 0);
        Make(Path.Combine("skipped", "set aside.mp4"), 400);                // clips Matthew chose not to post
        Make(Path.Combine("intermediate", "footage", "broll.mp4"), 400);    // working files, not his videos
        // A clip moved to Drive earlier: its record is here, its video is not.
        File.WriteAllText(Path.Combine(campaign, "output", "tiktok", "moved earlier.drive.json"), "{}");

        var found = CampaignDriveFiles.Find(campaign, "gdrive:WhopClipper/C", DateTime.UtcNow);

        Assert.Equal([episode, _clip], found.Files.Select(x => x.LocalPath));
        Assert.Equal(["source", "clip"], found.Files.Select(x => x.Group));
        Assert.Equal(["gdrive:WhopClipper/C/sources", "gdrive:WhopClipper/C"], found.Files.Select(x => x.RemoteFolder));
        // A clip frees its music-free preview too: the two travel together.
        Assert.Equal([900L, 4096L + 2048L], found.Files.Select(x => x.Bytes));
        Assert.Equal(1, found.AlreadyMoved);
        Assert.Equal(1, found.StillWriting);
    }

    [Fact]
    public async Task ASourceVideoIsRemovedFromThisPcOnlyOnceDriveHasIt()
    {
        var source = Path.Combine(_root, "workspace", "campaigns", "c", "source", "episode [abc123].mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var original = RandomNumberGenerator.GetBytes(3000);
        File.WriteAllBytes(source, original);
        var archive = Archive(new FakeTools(_drive));
        var folder = archive.RemoteFor("Double Coverage Podcast Clips") + "/" + CampaignDriveFiles.SourcesFolder;

        var moved = await archive.MoveFileAsync(folder, source, null, [SourceRow], CancellationToken.None);

        Assert.Equal(3000, moved.Bytes);
        Assert.False(File.Exists(source));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_drive, "WhopClipper", "Double Coverage Podcast Clips", "sources", "episode [abc123].mp4")));
        // What is left says where it went, and which source row used it.
        var record = DriveArchive.Read(source)!;
        Assert.Equal("gdrive:WhopClipper/Double Coverage Podcast Clips/sources", record.Remote);
        Assert.Equal(3000, Assert.Single(record.Files).Size);
        Assert.Equal([SourceRow], record.SourceIds!);
    }

    private static readonly Guid SourceRow = Guid.Parse("c8a3a531-6ec0-4b99-9fef-7b9a25d2fc2f");

    [Fact]
    public async Task EverythingMovedOutIsFoundAgainAndComesBackByteForByte()
    {
        var campaign = Path.Combine(_root, "workspace", "campaigns", "c");
        var source = Path.Combine(campaign, "source", "episode [dQw4w9WgXcQ].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var episode = RandomNumberGenerator.GetBytes(3000);
        File.WriteAllBytes(source, episode);
        var clip = File.ReadAllBytes(_clip);
        var archive = Archive(new FakeTools(_drive));
        await archive.MoveOutAsync("C", _clip, CancellationToken.None);
        await archive.MoveFileAsync(archive.RemoteFor("C") + "/sources", source, null, [SourceRow], CancellationToken.None);
        // A clip that never left is not listed.
        File.WriteAllBytes(Path.Combine(campaign, "output", "tiktok", "still here.mp4"), [1, 2, 3]);

        var found = CampaignDriveReturn.Find(campaign);

        Assert.Equal([_clip, source], found.Select(x => x.LocalPath));
        Assert.Equal(["clip", "source"], found.Select(x => x.Group));
        Assert.Equal([4096L + 2048L, 3000L], found.Select(x => x.Bytes));
        Assert.Equal([SourceRow], found[1].SourceIds);

        foreach (var file in found) await archive.BringBackAsync(file.LocalPath, CancellationToken.None);

        Assert.Equal(clip, File.ReadAllBytes(_clip));
        Assert.Equal(episode, File.ReadAllBytes(source));
        Assert.Empty(CampaignDriveReturn.Find(campaign));
    }

    [Fact]
    public async Task ASourceVideoThatArrivesDamagedStaysOnThisPc()
    {
        var source = Path.Combine(_root, "workspace", "campaigns", "c", "source", "episode.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(3000));
        var archive = Archive(new FakeTools(_drive) { CorruptUploads = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            archive.MoveFileAsync(archive.RemoteFor("C") + "/sources", source, null, null, CancellationToken.None));

        Assert.True(File.Exists(source));
        Assert.Null(DriveArchive.Read(source));
    }

    [Fact]
    public void AClipIsMatchedToTheEditPlanThatIsRenderingIt()
    {
        var plan = Guid.Parse("7536e59b-00d3-4a09-a3a5-56adb71bbdd4");
        (Guid, string)[] rendering = [(plan, "TJR Banks $138,000 On NASDAQ 📈.mp4")];
        // Named for its plan, as the narrated and podcast styles do.
        Assert.True(CampaignDriveFiles.BelongsTo(@"D:\out\TJR Banks $138,000 On NASDAQ 📈-7536e59b00d34a09a3a556adb71bbdd4.mp4", rendering));
        // Named for its render job, as the older styles do: matched by the plan's output name.
        Assert.True(CampaignDriveFiles.BelongsTo(@"D:\out\TJR Banks $138,000 On NASDAQ 📈-0123456789abcdef0123456789abcdef.mp4", rendering));
        Assert.False(CampaignDriveFiles.BelongsTo(@"D:\out\TJR Bets $3K On A Coin Flip 🪙-194fa26952744a91b6fa61b4d1206acf.mp4", rendering));
        Assert.False(CampaignDriveFiles.BelongsTo(@"D:\out\anything.mp4", []));
    }

    [Fact]
    public async Task AClipAlreadyInDriveIsNotMovedTwice()
    {
        var archive = Archive(new FakeTools(_drive));
        await archive.MoveOutAsync("Double Coverage Podcast Clips", _clip, CancellationToken.None);
        File.WriteAllBytes(_clip, [1]);   // a stray file where the clip used to be

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            archive.MoveOutAsync("Double Coverage Podcast Clips", _clip, CancellationToken.None));
        Assert.Contains("already in Google Drive", error.Message);
    }
}
