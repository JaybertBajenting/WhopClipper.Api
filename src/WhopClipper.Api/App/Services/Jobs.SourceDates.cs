using Microsoft.EntityFrameworkCore;

namespace WhopClipper.Api.App.Services;

public sealed partial class JobExecutionService
{
    /// <summary>
    /// Looks up the publish day of every YouTube source that has none yet, in all campaigns, a batch of videos per
    /// yt-dlp run. Sources added before dates were kept get theirs this way; new ones get it when inspected or
    /// clipped. A video yt-dlp cannot read is left to try again next time; one with no date is not asked again.
    /// </summary>
    /// <returns>How many sources were looked up.</returns>
    public async Task<int> FillSourceDatesAsync(CancellationToken cancellationToken)
    {
        var candidates = await db.SourceAssets.Where(x => x.Url != "").ToListAsync(cancellationToken);
        var missing = candidates
            .Where(x => FootageBin.YouTubeId(x.Url) is not null && !SourceDates.WasChecked(x.MetadataJson))
            .GroupBy(x => FootageBin.YouTubeId(x.Url)!)
            .ToDictionary(x => x.Key, x => x.ToList());
        var looked = 0;
        foreach (var batch in missing.Keys.Chunk(25))
        {
            var args = new List<string>([.. CookieArguments(), .. JsRuntimeArguments()])
            {
                // Gentle on YouTube: it throttles a PC that asks too much, and then caption downloads fail too.
                "--no-warnings", "--skip-download", "--ignore-errors", "--no-playlist", "--sleep-requests", "1",
                "--print", "%(id)s\t%(upload_date)s\t%(title)s",
            };
            args.AddRange(batch.Select(id => $"https://www.youtube.com/watch?v={id}"));
            var result = await processRunner.RunAsync(new ProcessInvocation(options.Value.YtDlpPath, args, workspace.Root,
                Timeout: TimeSpan.FromMinutes(10)), cancellationToken);
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || !missing.TryGetValue(parts[0], out var sources)) continue;
                foreach (var source in sources)
                {
                    source.MetadataJson = SourceDates.WithUploadDate(source.MetadataJson, parts[1]);
                    if (string.IsNullOrWhiteSpace(source.Title) && parts.Length > 2 && parts[2] is { Length: > 0 } title && title != "NA")
                        source.Title = title;
                    looked++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        if (looked > 0) logger.LogInformation("Found the publish date of {Count} source videos.", looked);
        return looked;
    }
}

/// <summary>
/// Fills in source videos' publish dates shortly after the app starts, then every half hour, so every campaign's
/// sources and clips can be listed newest video first without anyone pressing anything.
/// </summary>
public sealed class SourceDatesWorker(IServiceScopeFactory scopeFactory, ILogger<SourceDatesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of start-up and of the jobs it restores.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<JobExecutionService>().FillSourceDatesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not look up source video dates; trying again later.");
                }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
