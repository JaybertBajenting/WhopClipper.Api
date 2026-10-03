using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Data;

namespace WhopClipper.Api.App.Services;

/// <param name="FolderName">The campaign id the folder is named after, as 32 hex characters.</param>
public sealed record OrphanedCampaignFolder(string FolderName, long SizeBytes, int FileCount,
    DateTimeOffset LastModified);

/// <summary>
/// Finds campaign folders no campaign owns any more.
///
/// Deleting a campaign removes its rows and then its folder, in that order. If the folder cannot be
/// removed - a file held open by another program is the usual reason - the rows are already gone,
/// so the media is left on disk with nothing in the app pointing at it. On a workspace where a
/// single source runs to twelve gigabytes that is not a tidiness problem, it is disk that silently
/// stops coming back.
/// </summary>
public sealed class WorkspaceMaintenanceService(AppDbContext db, WorkspaceService workspace,
    ILogger<WorkspaceMaintenanceService> logger)
{
    private string CampaignsRoot => workspace.ResolveRelative("campaigns");

    public async Task<IReadOnlyList<OrphanedCampaignFolder>> FindOrphansAsync(CancellationToken cancellationToken)
    {
        var root = CampaignsRoot;
        if (!Directory.Exists(root)) return [];
        var known = (await db.Campaigns.AsNoTracking().Select(x => x.Id).ToListAsync(cancellationToken))
            .Select(x => x.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = new List<OrphanedCampaignFolder>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            // Only folders named like a campaign id are considered. Anything else in here was put
            // there by a person, and guessing that it is rubbish would be the wrong call.
            if (!Guid.TryParseExact(name, "N", out _) || known.Contains(name)) continue;
            var files = SafeEnumerate(directory);
            orphans.Add(new OrphanedCampaignFolder(name, files.Sum(x => x.Length), files.Count,
                files.Count == 0 ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero)
                    : files.Max(x => new DateTimeOffset(x.LastWriteTimeUtc, TimeSpan.Zero))));
        }
        return [.. orphans.OrderByDescending(x => x.SizeBytes)];
    }

    /// <summary>
    /// Deletes one orphaned folder. Whether it is an orphan is decided here rather than trusted
    /// from the request: a campaign could have been created between the scan and the click, and
    /// this removes downloaded media and finished renders.
    /// </summary>
    public async Task<bool> DeleteOrphanAsync(string folderName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(folderName, "N", out var id))
            throw new InvalidOperationException("That is not a campaign folder name.");
        if (await db.Campaigns.AnyAsync(x => x.Id == id, cancellationToken))
            throw new InvalidOperationException(
                "A campaign owns that folder now, so it is not an orphan. Delete the campaign instead.");

        var root = CampaignsRoot;
        var target = Path.Combine(root, folderName);
        WorkspaceService.EnsureInside(root, target);
        if (!Directory.Exists(target)) return false;
        Directory.Delete(target, recursive: true);
        logger.LogInformation("Removed orphaned campaign folder {Folder}", folderName);
        return true;
    }

    /// <summary>A folder mid-write, or one holding a locked file, must not fail the whole scan.</summary>
    private static List<FileInfo> SafeEnumerate(string directory)
    {
        try
        {
            return [.. new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
