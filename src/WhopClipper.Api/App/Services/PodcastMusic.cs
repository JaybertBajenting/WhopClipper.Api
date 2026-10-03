using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhopClipper.Api.App.Services;

/// <summary>The song entry is an editing choice. Never silently move it to fit the clip.</summary>
public static class PodcastMusic
{
    public static bool HasRenderedMusic(string? argumentsJson) => argumentsJson?.Contains("[musicbase]", StringComparison.Ordinal) == true;

    public static double RenderedVoiceVolume(string? argumentsJson, double fallback)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return fallback;
        var match = Regex.Match(argumentsJson, @"volume=([0-9.]+)\[voicenorm\]", RegexOptions.CultureInvariant);
        if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var volume)) return volume;
        // A remix adjusts an already-normalised stem. Its persisted VoiceVolume is the new absolute gain.
        return fallback;
    }

    public static double ResolveStart(double? planStart, double? libraryStart, double? songDuration = null)
    {
        var start = planStart ?? libraryStart ?? 0;
        if (!double.IsFinite(start) || start < 0)
            throw new ArgumentOutOfRangeException(nameof(planStart), "Music start must be a finite, non-negative number.");
        if (songDuration is > 0 && start >= songDuration)
            throw new ArgumentOutOfRangeException(nameof(planStart),
                $"Music starts at {start:0.###}s, but this file is only {songDuration:0.###}s long. Choose an earlier song segment.");
        return start;
    }

    /// <summary>Older renders may have moved the entry or changed the plan after baking the file.</summary>
    public static bool DiffersFromRenderedFile(string? argumentsJson, string? selectedPath, double selectedStart)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;
        string[]? args;
        try { args = JsonSerializer.Deserialize<string[]>(argumentsJson); }
        catch (JsonException) { return true; }
        if (args is null) return true;
        var graphIndex = Array.IndexOf(args, "-filter_complex");
        if (graphIndex < 0 || graphIndex + 1 >= args.Length) return true;
        var graph = args[graphIndex + 1];
        var music = Regex.Match(graph, @"\[(\d+):a\]([^;]*?)\[musicbase\]", RegexOptions.CultureInvariant);
        if (!music.Success) return selectedPath is not null;
        if (selectedPath is null) return true;
        var musicIndex = int.Parse(music.Groups[1].Value, CultureInfo.InvariantCulture);
        var input = 0;
        var previousInputEnd = 0;
        for (var index = 0; index + 1 < graphIndex; index++)
        {
            if (args[index] != "-i") continue;
            if (input++ != musicIndex) { previousInputEnd = index + 2; continue; }
            if (!string.Equals(args[index + 1], selectedPath, StringComparison.OrdinalIgnoreCase)) return true;
            var actualStart = 0d;
            for (var option = previousInputEnd; option + 1 < index; option++)
                if (args[option] == "-ss" && double.TryParse(args[option + 1], CultureInfo.InvariantCulture, out var seek))
                    actualStart = seek;
            var trim = Regex.Match(music.Groups[2].Value, @"atrim=start=([0-9.]+)", RegexOptions.CultureInvariant);
            if (trim.Success && double.TryParse(trim.Groups[1].Value, CultureInfo.InvariantCulture, out var trimStart))
                actualStart += trimStart;
            return Math.Abs(actualStart - selectedStart) > 0.001;
        }
        return true;
    }
}
