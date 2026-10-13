using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// A small still of a scheduled clip, for its card on the phone. YouTube keeps a scheduled clip private until its
/// time, so the phone can neither play it nor fetch its thumbnail; without this an upcoming clip is a title and a
/// time. The still travels inside the encrypted schedule, so it has to be tiny: the whole schedule is one Firestore
/// document of at most a megabyte.
/// </summary>
public sealed class PhoneThumbnails(WorkspaceService workspace, IProcessRunner processRunner, IOptions<AppOptions> options,
    ILogger<PhoneThumbnails> logger)
{
    /// <summary>All stills in one schedule, as base64 characters. The schedule itself is refused above 700,000 bytes.</summary>
    public const int MaxTotalCharacters = 420_000;

    /// <summary>One still. A 216-pixel-wide frame is 8 to 15 KB; anything far above that is not the frame asked for.</summary>
    public const int MaxBytes = 40_000;

    /// <summary>The still as base64 JPEG, or null when the clip's file is gone or FFmpeg cannot read it. Made once
    /// and kept, so the schedule's bytes stay the same from one five-second check to the next.</summary>
    public async Task<string?> GetAsync(PublishJob job, CancellationToken ct)
    {
        try
        {
            var directory = workspace.ResolveRelative(Path.Combine("phone", "thumbs"));
            var path = Path.Combine(directory, $"{job.Id:N}.jpg");
            if (!File.Exists(path))
            {
                if (string.IsNullOrWhiteSpace(job.OutputRelativePath)) return null;
                var video = workspace.ResolveRelative(job.OutputRelativePath);
                if (!File.Exists(video)) return null;
                Directory.CreateDirectory(directory);
                // Three seconds in: past the opening animation of every edit style, and a real frame of the clip.
                var result = await processRunner.RunAsync(new ProcessInvocation(options.Value.FfmpegPath,
                    ["-y", "-v", "error", "-ss", "3", "-i", video, "-frames:v", "1", "-vf", "scale=216:-2", "-q:v", "7", path],
                    directory, Timeout: TimeSpan.FromSeconds(30)), ct);
                if (result.ExitCode != 0 || !File.Exists(path)) return null;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return bytes.Length is > 0 and <= MaxBytes ? Convert.ToBase64String(bytes) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No phone still for scheduled clip {Job}", job.Id);
            return null;
        }
    }

    /// <summary>
    /// Which clips get their still when not all fit: the soonest first, since those are the ones about to be looked
    /// at. <paramref name="stills"/> is in schedule order; a null entry has no still to send.
    /// </summary>
    public static IReadOnlyList<string?> WithinBudget(IReadOnlyList<string?> stills, int maxCharacters = MaxTotalCharacters)
    {
        var used = 0;
        return stills.Select(still =>
        {
            if (still is null || used + still.Length > maxCharacters) return null;
            used += still.Length;
            return still;
        }).ToList();
    }
}
