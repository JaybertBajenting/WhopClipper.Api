using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Reads FFmpeg's <c>-progress</c> report as it streams in: blocks of key=value lines, each closed by a
/// <c>progress=</c> line. Tells <paramref name="report"/> how many seconds have been written, and how many bytes.
/// </summary>
public sealed partial class FfmpegProgressReader(Action<double, long?> report)
{
    private readonly StringBuilder _pending = new();
    private double? _seconds;
    private long? _bytes;

    public void Feed(string chunk)
    {
        _pending.Append(chunk);
        var text = _pending.ToString();
        var end = text.LastIndexOfAny(['\n', '\r']);
        if (end < 0)
        {
            if (_pending.Length > 65_536) _pending.Clear();
            return;
        }
        _pending.Clear().Append(text, end + 1, text.Length - end - 1);
        foreach (var line in text[..end].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var value = line[(equals + 1)..].Trim();
            switch (line[..equals].Trim())
            {
                // Negative at the very start, while FFmpeg is still seeking; "N/A" before anything is written.
                case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro):
                    _seconds = Math.Max(0, micro / 1_000_000d);
                    break;
                case "total_size" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size):
                    _bytes = size;
                    break;
                case "progress" when _seconds is { } seconds:
                    report(seconds, _bytes);
                    break;
            }
        }
    }

    /// <summary>Standard error with the progress report taken out, so an error message shows the actual error.</summary>
    public static string WithoutReports(string? text) => string.Join('\n', (text ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => !ReportLine().IsMatch(line)));

    [GeneratedRegex(@"^\s*[a-z0-9_]+=\S*\s*$")]
    private static partial Regex ReportLine();
}

public sealed partial class JobExecutionService
{
    /// <summary>
    /// Downloads a link pasted into a clip's B-roll editor into the campaign's footage: video only (the clip keeps its
    /// own sound), at most 1080p, and only the stretch asked for, capped at <see cref="FootageBin.MaxSeconds"/>.
    /// Progress is reported to the editor as it goes.
    /// </summary>
    private async Task DownloadFootageAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var request = Deserialize<FootageDownloadJobPayload>(job.PayloadJson);
        var plan = await db.EditPlans.AsNoTracking().SingleAsync(x => x.Id == request.EditPlanId, cancellationToken);
        var folder = FootageBin.Folder(workspace, plan.CampaignId);
        var item = FootageBin.Find(folder, request.FootageId)
            ?? throw new InvalidOperationException("This footage was removed before it downloaded.");
        try
        {
            FootageBin.ReportProgress(item.Id, new FootageProgress("Reading the video's details"));
            var link = await ProbeLinkAsync(item.Url, cancellationToken);
            item = item with { Title = link.Title ?? item.Url, Channel = link.Channel, SourceDurationSeconds = link.Duration };
            FootageBin.Write(folder, item);
            var from = Math.Max(0, item.FromSeconds ?? 0);
            var to = item.ToSeconds ?? (link.Duration ?? from + FootageBin.MaxSeconds);
            if (link.Duration is { } length) to = Math.Min(to, length);
            to = Math.Min(to, from + FootageBin.MaxSeconds);
            if (to - from < 1)
                throw new InvalidOperationException(link.Duration is { } whole && from >= whole - 1
                    ? $"The video is only {FormatClock(whole)} long, so nothing starts at {FormatClock(from)}."
                    : "That stretch of the video is shorter than a second.");
            job.Progress = .1;
            await db.SaveChangesAsync(cancellationToken);

            diskSpace.EnsureRoomForBytes((long)((to - from) * 500_000), item.Title, BrollProbeReserveBytes);
            FootageBin.ReportProgress(item.Id, new FootageProgress("Downloading", 0));
            var section = to - from;
            var path = await DownloadSectionAsync(item.Url, from, to, folder, item.Id.ToString("N"), 1080, false,
                cancellationToken, (seconds, bytes) => FootageBin.ReportProgress(item.Id,
                    new FootageProgress("Downloading", Math.Clamp(seconds / section, 0, 1), bytes)));
            FootageBin.ReportProgress(item.Id, new FootageProgress("Checking the file", 1));
            var probe = await ProbeVideoAsync(path, cancellationToken);
            item = item with
            {
                Status = FootageBin.Ready, FileName = Path.GetFileName(path), DurationSeconds = Math.Round(probe.DurationSeconds, 3),
                Width = probe.Width, Height = probe.Height, Error = null,
            };
            FootageBin.Write(folder, item);
            job.ResultJson = JsonSerializer.Serialize(new { footageId = item.Id, item.Title, item.DurationSeconds }, JsonDefaults.Serializer);
        }
        catch (Exception ex)
        {
            // A download that stopped for any reason, cancelling included, must not sit in the bin as "Downloading".
            FootageBin.Write(folder, item with
            {
                Status = FootageBin.Failed, Error = ex is OperationCanceledException ? "The download was cancelled." : ex.Message,
            });
            throw;
        }
        finally
        {
            FootageBin.EndProgress(item.Id);
        }
    }

    private static string FormatClock(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>The linked video's title, channel and length, or nulls when the site will not say.</summary>
    private async Task<(string? Title, string? Channel, double? Duration)> ProbeLinkAsync(string url, CancellationToken cancellationToken)
    {
        var args = new List<string>([.. CookieArguments(), .. JsRuntimeArguments()])
        {
            "--no-playlist", "--no-warnings", "--skip-download", "--print", "%(title)s", "--print", "%(duration)s",
            "--print", "%(channel,uploader)s", url
        };
        var result = await processRunner.RunAsync(new ProcessInvocation(options.Value.YtDlpPath, args, workspace.Root,
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.ExitCode != 0 || lines.Length == 0)
            throw new InvalidOperationException($"That link could not be opened. {Tail(result.StandardError)}");
        string? Value(int index) => index < lines.Length && lines[index] is { Length: > 0 } value && value != "NA" ? value : null;
        return (Value(0), Value(2),
            double.TryParse(Value(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null);
    }
}
