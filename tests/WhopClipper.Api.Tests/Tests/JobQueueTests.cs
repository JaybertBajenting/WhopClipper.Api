using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class JobQueueTests
{
    [Fact]
    public void QueueingNeverWaitsForRoomBehindALongBatch()
    {
        // A 130-clip re-do once filled a 100-slot queue, and every later job - a Re-render click too -
        // hung for about ten minutes until a queued clip finished.
        var queue = new JobQueue();
        for (var i = 0; i < 250; i++)
            Assert.True(queue.EnqueueAsync(Guid.NewGuid()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TheBrollEditorsJobsNeverQueueBehindAClipRun()
    {
        // Two pasted links sat "Downloading..." for an hour behind a nine-clip episode run in the one media lane.
        var queue = new JobQueue();
        var episode = Guid.NewGuid();
        var download = Guid.NewGuid();
        var handPlaced = Guid.NewGuid();
        var aiRepair = Guid.NewGuid();
        await queue.EnqueueAsync(episode, JobKind.SourcePipeline, "{}");
        await queue.EnqueueAsync(download, JobKind.FootageDownload, Json(new FootageDownloadJobPayload(Guid.NewGuid(), Guid.NewGuid())));
        await queue.EnqueueAsync(handPlaced, JobKind.BrollRepair, Json(new BrollRepairJobPayload(Guid.NewGuid(), 2, 4,
            Manual: [new ManualBrollShot(Guid.NewGuid(), 0, 2)])));
        await queue.EnqueueAsync(aiRepair, JobKind.BrollRepair, Json(new BrollRepairJobPayload(Guid.NewGuid(), 2, 4)));

        Assert.Equal([download, handPlaced], await Take(queue.ReadEditorAsync(CancellationToken.None), 2));
        Assert.Equal([episode, aiRepair], await Take(queue.ReadAllAsync(CancellationToken.None), 2));
    }

    [Fact]
    public async Task ADriveUploadWaitsBehindNothingAndHoldsNothingUp()
    {
        // An hour of uploading must not sit behind a clip run, nor take a render's or the B-roll editor's seat.
        var queue = new JobQueue();
        var episode = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var download = Guid.NewGuid();
        await queue.EnqueueAsync(episode, JobKind.SourcePipeline, "{}");
        var bringBack = Guid.NewGuid();
        await queue.EnqueueAsync(upload, JobKind.DriveUpload, Json(new DriveUploadJobPayload()));
        await queue.EnqueueAsync(download, JobKind.FootageDownload, Json(new FootageDownloadJobPayload(Guid.NewGuid(), Guid.NewGuid())));
        await queue.EnqueueAsync(bringBack, JobKind.DriveReturn, Json(new DriveReturnJobPayload()));

        Assert.Equal([upload, bringBack], await Take(queue.ReadUploadsAsync(CancellationToken.None), 2));
        Assert.Equal([episode], await Take(queue.ReadAllAsync(CancellationToken.None), 1));
        Assert.Equal([download], await Take(queue.ReadEditorAsync(CancellationToken.None), 1));
    }

    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonDefaults.Serializer);

    private static async Task<List<Guid>> Take(IAsyncEnumerable<Guid> lane, int count)
    {
        var taken = new List<Guid>();
        await foreach (var id in lane)
        {
            taken.Add(id);
            if (taken.Count == count) break;
        }
        return taken;
    }
}
