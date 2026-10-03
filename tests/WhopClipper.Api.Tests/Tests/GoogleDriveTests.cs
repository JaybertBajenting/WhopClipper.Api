using System.Net;
using System.Net.Http.Headers;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class DriveUrlTests
{
    [Theory]
    [InlineData("https://drive.google.com/file/d/1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN/view?usp=sharing", "1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN")]
    [InlineData("https://drive.google.com/open?id=1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN", "1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN")]
    [InlineData("https://drive.google.com/uc?export=download&id=1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN", "1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN")]
    public void ReadsAFileIdOutOfEveryShapeDriveHandsPeople(string url, string expected)
    {
        var target = Assert.IsType<DriveTarget>(GoogleDriveClient.TryParse(url));
        Assert.Equal(DriveTargetKind.File, target.Kind);
        Assert.Equal(expected, target.Id);
    }

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1WdlIPICk2UTXHItCq4UA7nfBVIoKzDDJ?usp=sharing")]
    [InlineData("https://drive.google.com/drive/u/0/folders/1WdlIPICk2UTXHItCq4UA7nfBVIoKzDDJ")]
    public void TellsAFolderApartFromAFile(string url)
    {
        var target = Assert.IsType<DriveTarget>(GoogleDriveClient.TryParse(url));
        // A folder cannot be downloaded, and queuing one anyway is what produced a wall of
        // identical failures from a single imported link.
        Assert.Equal(DriveTargetKind.Folder, target.Kind);
        Assert.Equal("1WdlIPICk2UTXHItCq4UA7nfBVIoKzDDJ", target.Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://docs.google.com/document/d/1abcdefghijklmno/edit")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void LeavesEverythingElseToYtDlp(string? url) => Assert.Null(GoogleDriveClient.TryParse(url));
}

public sealed class DriveFileTests
{
    private static DriveFile File(string type) => new("id", "name", type, 10);

    [Fact]
    public void RecognisesVideoByContentType()
    {
        Assert.True(File("video/mp4").IsVideo);
        Assert.True(File("VIDEO/QUICKTIME").IsVideo);
        // The watermark a brief links alongside its footage. Downloading it as a source is what
        // the type check exists to stop.
        Assert.False(File("image/png").IsVideo);
        Assert.False(File("application/pdf").IsVideo);
    }

    [Fact]
    public void PrefersTheRealFilenameOverTheOpaqueId()
    {
        using var response = new HttpResponseMessage();
        response.Content = new StringContent("");
        response.Content.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment") { FileName = "\"Zach x Lacy FINAL.mp4\"" };
        Assert.Equal("Zach x Lacy FINAL.mp4", GoogleDriveClient.FileNameFrom(response, "1E8xm8av"));
    }

    [Fact]
    public void FallsBackToTheIdWhenDriveSendsNoName()
    {
        using var response = new HttpResponseMessage { Content = new StringContent("") };
        Assert.Equal("1E8xm8av", GoogleDriveClient.FileNameFrom(response, "1E8xm8av"));
    }
}

/// <summary>
/// The size check that matters. Elsewhere the guard estimates from a duration; a Drive probe knows
/// the exact byte count before anything is written, and these files run to twelve gigabytes.
/// </summary>
public sealed class DriveDiskGuardTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static DiskSpaceGuard Guard(WorkspaceService workspace, long floorBytes) =>
        new(workspace, Microsoft.Extensions.Options.Options.Create(new AppOptions
        {
            WorkspaceRoot = workspace.Root,
            MinimumFreeDiskBytes = floorBytes,
            DiskHeadroomFactor = 2.5,
        }));

    [Fact]
    public void RefusesAFileThatCannotFitAndSaysHowBigItIs()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WorkspaceService(Microsoft.Extensions.Options.Options.Create(
                new AppOptions { WorkspaceRoot = root }));
            // A floor larger than the whole disk guarantees the refusal without depending on how
            // much free space the machine running the tests happens to have.
            var guard = Guard(workspace, long.MaxValue / 4);
            var error = Assert.Throws<InvalidOperationException>(
                () => guard.EnsureRoomForBytes(13_077_634_931, "Zach x Lacy FINAL.mp4"));
            Assert.Contains("Zach x Lacy FINAL.mp4", error.Message);
            Assert.Contains("12.2 GB", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LetsASmallFileThroughWhenThereIsPlentyOfRoom()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WorkspaceService(Microsoft.Extensions.Options.Options.Create(
                new AppOptions { WorkspaceRoot = root }));
            Guard(workspace, 0).EnsureRoomForBytes(1024, "tiny.mp4");
        }
        finally { Directory.Delete(root, true); }
    }
}
