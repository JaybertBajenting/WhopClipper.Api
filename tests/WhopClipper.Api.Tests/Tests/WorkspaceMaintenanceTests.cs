using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// Finding and removing campaign media nothing owns any more. This deletes folders outright, so the
/// tests are mostly about what it must refuse to touch.
/// </summary>
public sealed class WorkspaceMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-maint-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db;
    private readonly WorkspaceMaintenanceService _service;

    public WorkspaceMaintenanceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "campaigns"));
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("maint-" + Guid.NewGuid().ToString("N")).Options);
        var workspace = new WorkspaceService(Options.Create(new AppOptions { WorkspaceRoot = _root }));
        _service = new WorkspaceMaintenanceService(_db, workspace,
            NullLogger<WorkspaceMaintenanceService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeFolder(Guid id, long bytes = 1024)
    {
        var path = Path.Combine(_root, "campaigns", id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "output"));
        File.WriteAllBytes(Path.Combine(path, "output", "clip.mp4"), new byte[bytes]);
        return path;
    }

    private async Task<Campaign> AddCampaignAsync(Guid id)
    {
        var campaign = new Campaign { Id = id, Name = "Owned", RawInstructions = "x" };
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync();
        return campaign;
    }

    [Fact]
    public async Task ReportsAFolderNoCampaignOwns()
    {
        var orphanId = Guid.NewGuid();
        MakeFolder(orphanId, 4096);
        var orphans = await _service.FindOrphansAsync(CancellationToken.None);
        var found = Assert.Single(orphans);
        Assert.Equal(orphanId.ToString("N"), found.FolderName);
        Assert.Equal(4096, found.SizeBytes);
        Assert.Equal(1, found.FileCount);
    }

    [Fact]
    public async Task LeavesALiveCampaignsFolderAlone()
    {
        var id = Guid.NewGuid();
        await AddCampaignAsync(id);
        MakeFolder(id);
        Assert.Empty(await _service.FindOrphansAsync(CancellationToken.None));
    }

    /// <summary>
    /// Anything not named like a campaign id was put here deliberately. Treating it as rubbish
    /// because the app does not recognise it would be the wrong call.
    /// </summary>
    [Fact]
    public async Task IgnoresFoldersThatAreNotCampaignIdsAtAll()
    {
        Directory.CreateDirectory(Path.Combine(_root, "campaigns", "my-notes"));
        Directory.CreateDirectory(Path.Combine(_root, "campaigns", "backup-2026"));
        Assert.Empty(await _service.FindOrphansAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeletesAnOrphanAndItsContents()
    {
        var id = Guid.NewGuid();
        var path = MakeFolder(id);
        Assert.True(await _service.DeleteOrphanAsync(id.ToString("N"), CancellationToken.None));
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// The scan and the click are separated by however long the reader took. A campaign created in
    /// between owns that folder, and deleting it would take a live campaign's media with it.
    /// </summary>
    [Fact]
    public async Task RefusesOnceACampaignOwnsTheFolderAgain()
    {
        var id = Guid.NewGuid();
        var path = MakeFolder(id);
        await AddCampaignAsync(id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteOrphanAsync(id.ToString("N"), CancellationToken.None));
        Assert.Contains("owns that folder", error.Message);
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task RefusesAnythingThatIsNotACampaignFolderName(string name) =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteOrphanAsync(name, CancellationToken.None));

    [Fact]
    public async Task ReportsNothingRatherThanThrowingOnAFreshWorkspace()
    {
        Directory.Delete(Path.Combine(_root, "campaigns"), true);
        Assert.Empty(await _service.FindOrphansAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PutsTheBiggestOrphanFirstBecauseThatIsTheDiskWorthReclaiming()
    {
        MakeFolder(Guid.NewGuid(), 1024);
        MakeFolder(Guid.NewGuid(), 900_000);
        MakeFolder(Guid.NewGuid(), 50_000);
        var orphans = await _service.FindOrphansAsync(CancellationToken.None);
        Assert.Equal([900_000, 50_000, 1024], orphans.Select(x => x.SizeBytes));
    }
}
