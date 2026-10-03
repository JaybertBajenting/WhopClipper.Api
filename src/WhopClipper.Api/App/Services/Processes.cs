using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Contracts;

namespace WhopClipper.Api.App.Services;

/// <param name="OnStandardError">Called with each piece of standard error as it arrives, for progress reporting. It runs
/// on the reading thread, so it must be quick and must not touch anything that is not thread-safe.</param>
public sealed record ProcessInvocation(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    string? StandardInput = null, TimeSpan? Timeout = null, Action<string>? OnStandardError = null);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Duration);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}

public sealed class SafeProcessRunner(IOptions<AppOptions> options, ILogger<SafeProcessRunner> logger) : IProcessRunner
{
    private static readonly string[] EnvironmentAllowList =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA",
        "LOCALAPPDATA", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "LANG", "LC_ALL"
    ];

    public async Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocation.Executable)) throw new InvalidOperationException("Executable path is not configured.");
        var workingDirectory = Path.GetFullPath(invocation.WorkingDirectory);
        Directory.CreateDirectory(workingDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = invocation.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // yt-dlp and ffmpeg emit UTF-8. Without this the console codepage mangles any
            // non-ASCII character in a reported file path, and emoji in titles are common.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in invocation.Arguments) startInfo.ArgumentList.Add(argument);
        var inherited = EnvironmentAllowList.Select(x => (Name: x, Value: Environment.GetEnvironmentVariable(x))).Where(x => x.Value is not null).ToArray();
        startInfo.Environment.Clear();
        foreach (var variable in inherited) startInfo.Environment[variable.Name] = variable.Value!;
        // yt-dlp is a Python program; make it write UTF-8 rather than the console codepage.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(invocation.Executable)}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false, stopwatch.Elapsed);
        }

        var captureLimit = Math.Max(4_096, options.Value.MaxCapturedLogCharacters);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, captureLimit, null);
        var stderrTask = ReadBoundedAsync(process.StandardError, captureLimit, invocation.OnStandardError);
        using var timeout = new CancellationTokenSource(invocation.Timeout ?? TimeSpan.FromMinutes(options.Value.ProcessTimeoutMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        if (invocation.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), linked.Token);
                await process.StandardInput.FlushAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            finally { process.StandardInput.Close(); }
        }

        var timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        logger.LogInformation("Process {Executable} completed with exit code {ExitCode} after {Duration}",
            Path.GetFileName(invocation.Executable), process.ExitCode, stopwatch.Elapsed);
        return new ProcessResult(process.ExitCode, stdout, stderr, timedOut, stopwatch.Elapsed);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters, Action<string>? onRead)
    {
        var buffer = new char[4_096];
        var captured = new StringBuilder(Math.Min(maxCharacters, 32_768));
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            if (onRead is not null)
            {
                // A progress reader that throws must not lose the process's output or its exit code.
                try { onRead(new string(buffer, 0, count)); } catch (Exception) { }
            }
            captured.Append(buffer, 0, count);
            var overflow = captured.Length - maxCharacters;
            if (overflow > 0) captured.Remove(0, overflow);
        }
        return captured.ToString();
    }
}

public sealed class DependencyService(IProcessRunner runner, IOptions<AppOptions> options, WorkspaceService workspace,
    DatabaseReadinessService databaseReadiness)
{
    public async Task<IReadOnlyList<ToolDependencyDto>> CheckAsync(CancellationToken cancellationToken)
    {
        var configured = new[]
        {
            ("yt-dlp", options.Value.YtDlpPath, new[] { "--version" }),
            ("ffmpeg", options.Value.FfmpegPath, new[] { "-version" }),
            ("ffprobe", options.Value.FfprobePath, new[] { "-version" }),
            ("Claude CLI", options.Value.ClaudePath, new[] { "--version" }),
            ("Codex CLI", options.Value.CodexPath, new[] { "--version" })
        };
        var readiness = await databaseReadiness.CheckAsync(cancellationToken);
        var results = new List<ToolDependencyDto>
        {
            new("PostgreSQL", "ConnectionStrings:Postgres", readiness.Ready,
                readiness.Ready ? "Connected; migrations current" : null, readiness.Ready ? null : readiness.Error)
        };
        foreach (var tool in configured)
        {
            var result = await runner.RunAsync(new ProcessInvocation(tool.Item2, tool.Item3, workspace.Root, Timeout: TimeSpan.FromSeconds(8)), cancellationToken);
            var output = string.Join(' ', (result.StandardOutput + "\n" + result.StandardError).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(2));
            results.Add(new ToolDependencyDto(tool.Item1, tool.Item2, result.ExitCode == 0, result.ExitCode == 0 ? output : null,
                result.ExitCode == 0 ? null : output));
        }
        return results;
    }
}

/// <param name="SchemaJson">The schema handed to the CLI. Null means the highlight schema, which is
/// what every call used before the podcast-split style needed stories and B-roll plans too.
/// Codex reads <paramref name="SchemaPath"/>, so write the same schema there.</param>
public sealed record AiProviderRequest(string Prompt, string SchemaPath, string WorkingDirectory, string? SchemaJson = null);
public sealed record AiHighlight(double StartSeconds, double EndSeconds, string Hook, string Rationale,
    double Score, double Confidence, JsonElement EditPlan);
public sealed record AiProviderResponse(string Provider, IReadOnlyList<AiHighlight> Highlights, string RawOutput, int ExitCode);
public sealed record AiProviderAvailability(bool Available, string? Error);

public interface IAiMediaProvider
{
    string Name { get; }
    Task<AiProviderAvailability> CheckAvailabilityAsync(string workingDirectory, CancellationToken cancellationToken);
    Task<AiProviderResponse> AnalyzeAsync(AiProviderRequest request, CancellationToken cancellationToken);
    /// <summary>Runs any schema and returns the payload <paramref name="accept"/> recognises, with the
    /// same rate-limit retries as highlight analysis.</summary>
    Task<JsonElement> RunStructuredAsync(AiProviderRequest request, Func<JsonElement, bool> accept,
        CancellationToken cancellationToken);
}

public abstract class CliAiProvider(IProcessRunner runner) : IAiMediaProvider
{
    public abstract string Name { get; }
    protected abstract string ExecutablePath { get; }
    protected abstract ProcessInvocation CreateInvocation(AiProviderRequest request);

    public async Task<AiProviderAvailability> CheckAvailabilityAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessInvocation(ExecutablePath, ["--version"], workingDirectory,
            Timeout: TimeSpan.FromSeconds(8)), cancellationToken);
        var error = string.Join(' ', (result.StandardError + "\n" + result.StandardOutput)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(2));
        if (result.ExitCode != 0) return new(false, error);

        // `--version` succeeds even when the session has expired, so it alone would report a
        // provider as ready that cannot answer a single request. Probe authentication too.
        var probe = await RunAuthenticationProbeAsync(workingDirectory, cancellationToken);
        return probe is null ? new(true, null) : new(false, probe);
    }

    /// <summary>Returns null when the CLI can actually answer, or a reason when it cannot.</summary>
    protected abstract Task<string?> RunAuthenticationProbeAsync(string workingDirectory, CancellationToken cancellationToken);

    protected static string? ReadProbeFailure(ProcessResult result)
    {
        if (result.TimedOut) return "The CLI did not respond in time.";
        var combined = result.StandardError + "\n" + result.StandardOutput;
        if (result.ExitCode == 0 && !combined.Contains("authenticat", StringComparison.OrdinalIgnoreCase)) return null;
        var lines = combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        return lines.Length == 0 ? $"The CLI exited with code {result.ExitCode}." : string.Join(" | ", lines.Take(3));
    }

    /// <summary>Attempts, and the wait before each retry. Long enough to outlast a short rate limit
    /// without stalling a queue for the rest of the night.</summary>
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)];

    /// <summary>
    /// A rate limit is the queue's problem, not this source's. An overnight run lost a source to a
    /// single 429 after the call had already been paid for, so transient refusals are waited out
    /// here. Anything else - a bad schema, an expired login - repeats identically and costs money
    /// each time, so it is raised on the first attempt.
    /// </summary>
    internal static bool IsTransient(ProcessResult result)
    {
        if (result.ExitCode == 0) return false;
        var combined = result.StandardError + "\n" + result.StandardOutput;
        return combined.Contains("api_error_status\":429", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("api_error_status\":529", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AiProviderResponse> AnalyzeAsync(AiProviderRequest request, CancellationToken cancellationToken)
    {
        var (result, output) = await RunWithRetriesAsync(request, cancellationToken);
        var highlights = AiOutputParser.Parse(output);
        if (highlights.Count == 0)
            throw new InvalidOperationException($"{Name} returned no schema-valid highlights. {Describe(result)}");
        return new AiProviderResponse(Name, highlights, output, result.ExitCode);
    }

    public async Task<JsonElement> RunStructuredAsync(AiProviderRequest request, Func<JsonElement, bool> accept,
        CancellationToken cancellationToken)
    {
        var (result, output) = await RunWithRetriesAsync(request, cancellationToken);
        return StructuredOutput.Extract(output, accept)
            ?? throw new InvalidOperationException($"{Name} returned nothing matching the schema. {Describe(result)}");
    }

    private async Task<(ProcessResult Result, string Output)> RunWithRetriesAsync(AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(CreateInvocation(request), cancellationToken);
        for (var attempt = 0; attempt < RetryDelays.Length && IsTransient(result); attempt++)
        {
            await Task.Delay(RetryDelays[attempt], cancellationToken);
            result = await runner.RunAsync(CreateInvocation(request), cancellationToken);
        }
        if (result.TimedOut) throw new TimeoutException($"{Name} timed out. {Describe(result)}");
        if (result.ExitCode != 0) throw new InvalidOperationException($"{Name} failed (exit {result.ExitCode}). {Describe(result)}");
        return (result, await GetParserOutputAsync(request, result, cancellationToken));
    }

    /// <summary>
    /// Both CLIs report failures such as an expired login on standard output, so an error
    /// built only from standard error is frequently empty and tells the user nothing.
    /// </summary>
    private static string Describe(ProcessResult result)
    {
        var lines = (result.StandardError + "\n" + result.StandardOutput)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
        if (lines.Length == 0) return "The CLI produced no output.";
        var detail = string.Join(" | ", lines.TakeLast(4));
        return detail.Length <= 1200 ? detail : detail[..1200];
    }

    protected virtual Task<string> GetParserOutputAsync(AiProviderRequest request, ProcessResult result,
        CancellationToken cancellationToken) => Task.FromResult(result.StandardOutput);
}

public sealed class ClaudeCliProvider(IProcessRunner runner, IOptions<AppOptions> options) : CliAiProvider(runner)
{
    public override string Name => "Claude";
    protected override string ExecutablePath => options.Value.ClaudePath;
    protected override ProcessInvocation CreateInvocation(AiProviderRequest request) => new(options.Value.ClaudePath,
        // Structured output is delivered through a tool call, so a single turn is not enough:
        // the run stops with stop_reason "tool_use" before the JSON is produced. Plan mode
        // and safe mode remain the boundary that keeps the run read-only.
        ["-p", "--output-format", "json", "--json-schema", request.SchemaJson ?? AiSchemas.HighlightSchema, "--max-turns", "8",
            "--safe-mode", "--no-session-persistence", "--permission-mode", "plan"],
        request.WorkingDirectory, request.Prompt);

    protected override async Task<string?> RunAuthenticationProbeAsync(string workingDirectory, CancellationToken cancellationToken) =>
        ReadProbeFailure(await runner.RunAsync(new ProcessInvocation(options.Value.ClaudePath,
            ["-p", "--output-format", "text", "--tools", "", "--max-turns", "1", "--no-session-persistence",
                "--permission-mode", "plan"],
            workingDirectory, "Reply with exactly: OK", TimeSpan.FromSeconds(45)), cancellationToken));
}

public sealed class CodexCliProvider(IProcessRunner runner, IOptions<AppOptions> options) : CliAiProvider(runner)
{
    public override string Name => "Codex";
    protected override string ExecutablePath => options.Value.CodexPath;
    protected override ProcessInvocation CreateInvocation(AiProviderRequest request) => new(options.Value.CodexPath,
        ["exec", "--ephemeral", "--sandbox", "read-only", "--ignore-user-config", "--ignore-rules",
            "--skip-git-repo-check", "--json",
            "--output-schema", request.SchemaPath, "--output-last-message", FinalMessagePath(request), "-"],
        request.WorkingDirectory, request.Prompt);

    protected override async Task<string> GetParserOutputAsync(AiProviderRequest request, ProcessResult result,
        CancellationToken cancellationToken)
    {
        var finalPath = FinalMessagePath(request);
        if (!File.Exists(finalPath)) return result.StandardOutput;
        var finalMessage = await File.ReadAllTextAsync(finalPath, cancellationToken);
        return string.IsNullOrWhiteSpace(finalMessage) ? result.StandardOutput : finalMessage;
    }

    protected override async Task<string?> RunAuthenticationProbeAsync(string workingDirectory, CancellationToken cancellationToken) =>
        ReadProbeFailure(await runner.RunAsync(new ProcessInvocation(options.Value.CodexPath,
            ["exec", "--ephemeral", "--sandbox", "read-only", "--ignore-user-config", "--skip-git-repo-check", "-"],
            workingDirectory, "Reply with exactly: OK", TimeSpan.FromSeconds(45)), cancellationToken));

    private static string FinalMessagePath(AiProviderRequest request) => Path.Combine(request.WorkingDirectory, "codex-last-message.json");
}

public static class AiSchemas
{
    // No $schema declaration: the Claude CLI validates the schema locally and rejects a
    // remote meta-schema reference it cannot resolve. The schema body is unchanged.
    public const string HighlightSchema = """
    {"title":"WhopClipperHighlightAnalysisV1","type":"object","additionalProperties":false,"required":["highlights"],"properties":{"highlights":{"type":"array","minItems":1,"maxItems":10,"items":{"type":"object","additionalProperties":false,"required":["startSeconds","endSeconds","hook","rationale","score","confidence","editPlan"],"properties":{"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},"hook":{"type":"string"},"rationale":{"type":"string"},"score":{"type":"number","minimum":0,"maximum":1},"confidence":{"type":"number","minimum":0,"maximum":1},"editPlan":{"type":"object","additionalProperties":false,"required":["framing","burnSubtitles","subtitleStyle","ctaOverlayText","musicAssetId","musicVolume","musicDuckingDb","soundEffects","overlays"],"properties":{"framing":{"type":"string","enum":["CenterCrop","Fit","Letterbox","SmartCrop"]},"burnSubtitles":{"type":"boolean"},"subtitleStyle":{"type":["string","null"]},"ctaOverlayText":{"type":["string","null"]},"musicAssetId":{"type":["string","null"]},"musicVolume":{"type":"number","minimum":0,"maximum":1},"musicDuckingDb":{"type":"number","minimum":-40,"maximum":0},"soundEffects":{"type":"array","maxItems":20,"items":{"type":"object","additionalProperties":false,"required":["assetId","atSeconds","gainDb"],"properties":{"assetId":{"type":"string"},"atSeconds":{"type":"number","minimum":0},"gainDb":{"type":"number","minimum":-60,"maximum":12}}}},"overlays":{"type":"array","maxItems":20,"items":{"type":"object","additionalProperties":false,"required":["text","startSeconds","endSeconds","position","fontSize","color"],"properties":{"text":{"type":"string"},"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},"position":{"type":"string","enum":["Top","Center","Bottom"]},"fontSize":{"type":"integer","minimum":12,"maximum":200},"color":{"type":"string"}}}}}}}}}}}
    """;
}

public static class AiOutputParser
{
    public static IReadOnlyList<AiHighlight> Parse(string output)
    {
        var direct = ParseJsonPayload(StripFence(output));
        if (direct.Count > 0) return direct;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var eventType) ||
                    eventType.GetString() != "item.completed" || !root.TryGetProperty("item", out var item) ||
                    item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var itemType) ||
                    itemType.GetString() != "agent_message" || !item.TryGetProperty("text", out var text) ||
                    text.ValueKind != JsonValueKind.String) continue;
                var parsed = ParseJsonPayload(StripFence(text.GetString() ?? string.Empty));
                if (parsed.Count > 0) return parsed;
            }
            catch (JsonException) { }
        }
        return [];
    }

    private static IReadOnlyList<AiHighlight> ParseJsonPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (root.TryGetProperty("structured_output", out var structured))
                return structured.ValueKind == JsonValueKind.String
                    ? ParseJsonPayload(StripFence(structured.GetString() ?? string.Empty))
                    : ParseHighlights(structured);
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                return ParseJsonPayload(StripFence(result.GetString() ?? string.Empty));
            return ParseHighlights(root);
        }
        catch (JsonException) { return []; }
    }

    // A dash holding two halves of a hook together reads as machine-written on a title card, so
    // it is rewritten to a comma. Only separator dashes go: em, en and friends anywhere, and a
    // plain hyphen only when it stands alone between spaces. A hyphen inside a word is left
    // intact, because "co-op" must not become "coop".
    // \u2010-\u2015 covers hyphen through horizontal bar; \u2212 is the minus sign. Written as
    // escapes so the class cannot be broken by how this file is re-encoded.
    private static readonly Regex SeparatorDash = new("\\s*[\u2010-\u2015\u2212]\\s*|\\s+-\\s+",
        RegexOptions.Compiled);

    internal static string CleanHookText(string hook)
    {
        var text = SeparatorDash.Replace(hook, ", ");
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Rewriting can butt the new comma against punctuation that was already there.
        text = Regex.Replace(text, @",\s*(?=[,.;:!?])", string.Empty);
        return text.Trim().Trim(',').Trim();
    }

    private static IReadOnlyList<AiHighlight> ParseHighlights(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasOnlyProperties(root, "highlights") ||
            !root.TryGetProperty("highlights", out var highlights) || highlights.ValueKind != JsonValueKind.Array ||
            highlights.GetArrayLength() is < 1 or > 10) return [];
        var parsed = new List<AiHighlight>();
        foreach (var item in highlights.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !HasOnlyProperties(item, "startSeconds", "endSeconds", "hook",
                    "rationale", "score", "confidence", "editPlan") ||
                !item.TryGetProperty("startSeconds", out var start) || !start.TryGetDouble(out var startValue) || startValue < 0 ||
                !item.TryGetProperty("endSeconds", out var end) || !end.TryGetDouble(out var endValue) || endValue <= startValue ||
                !item.TryGetProperty("hook", out var hook) || hook.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("rationale", out var rationale) || rationale.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("score", out var score) || !score.TryGetDouble(out var scoreValue) || scoreValue is < 0 or > 1 ||
                !item.TryGetProperty("confidence", out var confidence) || !confidence.TryGetDouble(out var confidenceValue) || confidenceValue is < 0 or > 1 ||
                !item.TryGetProperty("editPlan", out var plan) || !ValidateEditPlan(plan, endValue - startValue))
                return [];
            parsed.Add(new AiHighlight(startValue, endValue, CleanHookText(hook.GetString()!),
                rationale.GetString()!, scoreValue, confidenceValue, plan.Clone()));
        }
        return parsed;
    }

    private static bool ValidateEditPlan(JsonElement plan, double duration)
    {
        if (plan.ValueKind != JsonValueKind.Object || !HasOnlyProperties(plan, "framing", "burnSubtitles", "subtitleStyle",
                "ctaOverlayText", "musicAssetId", "musicVolume", "musicDuckingDb", "soundEffects", "overlays") ||
            !TryString(plan, "framing", out var framing) || framing is not ("CenterCrop" or "Fit" or "Letterbox" or "SmartCrop") ||
            !plan.TryGetProperty("burnSubtitles", out var burn) || burn.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !TryNullableString(plan, "subtitleStyle") || !TryNullableString(plan, "ctaOverlayText") ||
            !TryNullableGuid(plan, "musicAssetId") || !TryBoundedNumber(plan, "musicVolume", 0, 1, out _) ||
            !TryBoundedNumber(plan, "musicDuckingDb", -40, 0, out _) ||
            !plan.TryGetProperty("soundEffects", out var effects) || effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() > 20 ||
            !plan.TryGetProperty("overlays", out var overlays) || overlays.ValueKind != JsonValueKind.Array || overlays.GetArrayLength() > 20)
            return false;
        foreach (var effect in effects.EnumerateArray())
        {
            if (effect.ValueKind != JsonValueKind.Object || !HasOnlyProperties(effect, "assetId", "atSeconds", "gainDb") ||
                !TryRequiredGuid(effect, "assetId") || !TryBoundedNumber(effect, "atSeconds", 0, duration, out _) ||
                !TryBoundedNumber(effect, "gainDb", -60, 12, out _)) return false;
        }
        foreach (var overlay in overlays.EnumerateArray())
        {
            if (overlay.ValueKind != JsonValueKind.Object || !HasOnlyProperties(overlay, "text", "startSeconds", "endSeconds",
                    "position", "fontSize", "color") || !TryString(overlay, "text", out var text) || text.Length > 500 ||
                !TryBoundedNumber(overlay, "startSeconds", 0, duration, out var start) ||
                !TryBoundedNumber(overlay, "endSeconds", 0, duration, out var end) || end <= start ||
                !TryString(overlay, "position", out var position) || position is not ("Top" or "Center" or "Bottom") ||
                !overlay.TryGetProperty("fontSize", out var font) || !font.TryGetInt32(out var fontSize) || fontSize is < 12 or > 200 ||
                !TryString(overlay, "color", out var color) || color.Length > 20) return false;
        }
        return true;
    }

    private static bool HasOnlyProperties(JsonElement value, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        var properties = value.EnumerateObject().ToArray();
        return properties.Length == names.Count && properties.All(x => names.Contains(x.Name));
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        result = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryNullableString(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.String or JsonValueKind.Null;
    private static bool TryNullableGuid(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
        (property.ValueKind == JsonValueKind.Null || property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out _));
    private static bool TryRequiredGuid(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out _);
    private static bool TryBoundedNumber(JsonElement value, string name, double minimum, double maximum, out double result)
    {
        result = 0;
        return value.TryGetProperty(name, out var property) && property.TryGetDouble(out result) &&
               double.IsFinite(result) && result >= minimum && result <= maximum;
    }

    private static string StripFence(string value)
    {
        var candidate = value.Trim();
        if (!candidate.StartsWith("```", StringComparison.Ordinal)) return candidate;
        var firstLine = candidate.IndexOf('\n');
        var lastFence = candidate.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine ? candidate[(firstLine + 1)..lastFence].Trim() : candidate;
    }
}

public static partial class FfmpegArgumentBuilder
{
    private static readonly HashSet<string> FramingModes = new(StringComparer.OrdinalIgnoreCase)
        { "CenterCrop", "SmartCrop", "Fit", "Letterbox" };
    private static readonly HashSet<string> OverlayPositions = new(StringComparer.OrdinalIgnoreCase)
        { "Top", "Center", "Bottom" };
    private static readonly HashSet<string> FlashColors = new(StringComparer.OrdinalIgnoreCase) { "Green", "Red" };

    public static IReadOnlyList<string> Build(string sourcePath, string outputPath, double start, double end,
        int width, int height, int fps, int crf, bool normalizeAudio)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        if (width is < 240 or > 7680 || height is < 240 or > 7680) throw new ArgumentOutOfRangeException(nameof(width));
        var duration = end - start;
        var filter = $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},fps={fps}";
        var args = new List<string>
        {
            "-hide_banner", "-y", "-ss", start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", sourcePath, "-t", duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-vf", filter, "-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(),
            "-c:a", "aac", "-b:a", "192k"
        };
        if (normalizeAudio) args.AddRange(["-af", "loudnorm=I=-14:TP=-1.5:LRA=11"]);
        args.AddRange(["-movflags", "+faststart", outputPath]);
        return args;
    }

    public static IReadOnlyList<string> Build(FfmpegRenderSpec spec)
    {
        if (spec.EndSeconds <= spec.StartSeconds) throw new ArgumentOutOfRangeException(nameof(spec.EndSeconds));
        if (spec.Width is < 240 or > 7680 || spec.Height is < 240 or > 7680) throw new ArgumentOutOfRangeException(nameof(spec.Width));
        if (spec.FramesPerSecond is < 12 or > 120) throw new ArgumentOutOfRangeException(nameof(spec.FramesPerSecond));
        if (spec.Crf is < 0 or > 51) throw new ArgumentOutOfRangeException(nameof(spec.Crf));
        if (!FramingModes.Contains(spec.Framing)) throw new ArgumentException("Unsupported framing mode.", nameof(spec.Framing));
        if (spec.MusicVolume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.MusicVolume));
        if (spec.MusicDuckingDb is < -40 or > 0) throw new ArgumentOutOfRangeException(nameof(spec.MusicDuckingDb));

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var duration = spec.EndSeconds - spec.StartSeconds;
        var durationText = duration.ToString("0.###", invariant);
        var args = new List<string>
        {
            "-hide_banner", "-y", "-ss", spec.StartSeconds.ToString("0.###", invariant), "-i", spec.SourcePath
        };

        var nextInput = 1;
        int? musicInput = null;
        if (!string.IsNullOrWhiteSpace(spec.MusicPath))
        {
            musicInput = nextInput++;
            args.AddRange(["-stream_loop", "-1", "-t", durationText, "-i", spec.MusicPath]);
        }
        var soundEffectInputs = new List<(int Input, FfmpegSoundEffectInput Effect)>();
        foreach (var effect in spec.SoundEffects)
        {
            if (effect.AtSeconds < 0 || effect.AtSeconds > duration) throw new ArgumentOutOfRangeException(nameof(spec.SoundEffects));
            if (effect.GainDb is < -60 or > 12) throw new ArgumentOutOfRangeException(nameof(spec.SoundEffects));
            soundEffectInputs.Add((nextInput++, effect));
            args.AddRange(["-i", effect.Path]);
        }
        // Card overlays arrive as pre-rendered transparent PNGs and are composited below, so
        // they need their own inputs alongside the logo.
        var cardInputs = new List<(int Input, FfmpegOverlayInput Overlay)>();
        foreach (var overlay in spec.Overlays)
        {
            if (string.IsNullOrWhiteSpace(overlay.CardImagePath)) continue;
            if (overlay.YFraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.Overlays));
            cardInputs.Add((nextInput++, overlay));
            args.AddRange(["-i", overlay.CardImagePath!]);
        }

        int? logoInput = null;
        if (spec.Logo is { } logo)
        {
            if (!OverlayPositions.Contains(logo.Position)) throw new ArgumentException("Invalid logo position.", nameof(spec.Logo));
            if (logo.WidthFraction is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.Logo));
            if (logo.MarginFraction is < 0 or > 0.45) throw new ArgumentOutOfRangeException(nameof(spec.Logo));
            if (logo.Opacity is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spec.Logo));
            if (logo.CropRect is not null && !CropRectPattern().IsMatch(logo.CropRect))
                throw new ArgumentException("Invalid logo crop rectangle.", nameof(spec.Logo));
            logoInput = nextInput++;
            args.AddRange(["-i", logo.Path]);
        }

        var videoFilters = new List<string>();
        if (spec.Framing.Equals("Fit", StringComparison.OrdinalIgnoreCase) ||
            spec.Framing.Equals("Letterbox", StringComparison.OrdinalIgnoreCase))
            videoFilters.Add($"scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=decrease,pad={spec.Width}:{spec.Height}:(ow-iw)/2:(oh-ih)/2:color=black");
        else if (spec.SubjectTrack is { Keyframes.Count: > 0 } track)
            videoFilters.AddRange(BuildTrackedCrop(track, spec.Width, spec.Height, invariant));
        else
            videoFilters.Add($"scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=increase,crop={spec.Width}:{spec.Height}");
        videoFilters.Add($"fps={spec.FramesPerSecond}");

        // Both effects go on the footage before captions and branding are drawn: a reference
        // reaction beat tints and punches the picture while the caption on top of it stays
        // upright and its normal colour.
        var zooms = (spec.ZoomPunches ?? []).Where(x => x.DurationSeconds > 0).OrderBy(x => x.AtSeconds).ToList();
        if (zooms.Count > 0)
        {
            foreach (var punch in zooms)
                if (punch.AtSeconds < 0 || punch.AtSeconds > duration || punch.Scale is < 1 or > 4)
                    throw new ArgumentOutOfRangeException(nameof(spec.ZoomPunches));
            // Nested so the first matching window wins; anything outside them stays at 1.
            var z = "1";
            foreach (var punch in Enumerable.Reverse(zooms))
            {
                var a = punch.AtSeconds.ToString("0.###", invariant);
                var d = punch.DurationSeconds.ToString("0.###", invariant);
                var gain = (punch.Scale - 1).ToString("0.###", invariant);
                z = $"if(between(in_time\\,{a}\\,{a}+{d})\\,1+{gain}*(in_time-{a})/{d}\\,{z})";
            }
            videoFilters.Add($"zoompan=z='{z}':d=1:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':" +
                $"s={spec.Width}x{spec.Height}:fps={spec.FramesPerSecond}");
        }

        var flashes = (spec.ColorFlashes ?? []).Where(x => x.DurationSeconds > 0).OrderBy(x => x.AtSeconds).ToList();
        foreach (var flash in flashes)
            if (flash.AtSeconds < 0 || flash.AtSeconds > duration || flash.Intensity is <= 0 or > 1 ||
                !FlashColors.Contains(flash.Color))
                throw new ArgumentOutOfRangeException(nameof(spec.ColorFlashes));
        // One mixer per colour: the reference edits use a green wash and a bass-heavy red one,
        // and a single filter cannot be two colours at different moments.
        foreach (var group in flashes.GroupBy(x => x.Color, StringComparer.OrdinalIgnoreCase))
        {
            // Disjoint windows, so summing the tests behaves as an OR for the enable flag.
            var windows = string.Join('+', group.Select(x =>
                $"between(t\\,{x.AtSeconds.ToString("0.###", invariant)}\\," +
                $"{(x.AtSeconds + x.DurationSeconds).ToString("0.###", invariant)})"));
            var lift = group.First().Intensity;
            var up = (1 + lift).ToString("0.###", invariant);
            var down = (1 - lift).ToString("0.###", invariant);
            videoFilters.Add(group.Key.Equals("Red", StringComparison.OrdinalIgnoreCase)
                ? $"colorchannelmixer=rr={up}:gg={down}:bb={down}:enable='{windows}'"
                : $"colorchannelmixer=rr={down}:gg={up}:bb={down}:enable='{windows}'");
        }

        if (!string.IsNullOrWhiteSpace(spec.SubtitlePath))
        {
            // The subtitles filter reads whatever clock the frames carry, so the two have to be
            // put on the same one. A track downloaded alongside the source is stamped against
            // the whole video: push the frames onto the source clock for the lookup, then pull
            // them straight back. Captions built per clip are already rebased to zero, and
            // pushing those forward lands every event past the end of the clip - which is how
            // a whole caption track can silently vanish from the render.
            var onSourceClock = spec.SubtitleTimeBase == SubtitleTimeBase.SourceAbsolute;
            var sourceOffset = spec.StartSeconds.ToString("0.###", invariant);
            videoFilters.Add(onSourceClock ? $"setpts=PTS-STARTPTS+{sourceOffset}/TB" : "setpts=PTS-STARTPTS");
            var subtitleFilter = $"subtitles='{EscapeFilterPath(spec.SubtitlePath)}'";
            subtitleFilter += spec.SubtitleStyle?.Trim().ToLowerInvariant() switch
            {
                "bold" => ":force_style='FontName=Arial,FontSize=22,Bold=1,Outline=3,Shadow=0,Alignment=2'",
                "minimal" => ":force_style='FontName=Arial,FontSize=18,Bold=0,Outline=2,Shadow=0,Alignment=2'",
                "karaoke" => ":force_style='FontName=Arial,FontSize=22,Bold=1,PrimaryColour=&H0000FFFF,Outline=3,Alignment=2'",
                _ => string.Empty
            };
            videoFilters.Add(subtitleFilter);
            if (onSourceClock) videoFilters.Add($"setpts=PTS-{sourceOffset}/TB");
        }
        var overlayFontPath = spec.Overlays.Count > 0 ? ResolveOverlayFontPath(spec.OverlayFontPath) : null;
        foreach (var overlay in spec.Overlays)
        {
            if (!OverlayPositions.Contains(overlay.Position) || overlay.EndSeconds <= overlay.StartSeconds ||
                overlay.StartSeconds < 0 || overlay.EndSeconds > duration || overlay.FontSize is < 12 or > 200 ||
                !IsSafeColor(overlay.Color))
                throw new ArgumentException("Invalid overlay settings.", nameof(spec.Overlays));
            // Cards are composited from an image further down rather than drawn here.
            if (!string.IsNullOrWhiteSpace(overlay.CardImagePath)) continue;
            var y = overlay.Position.ToLowerInvariant() switch
            {
                "top" => "h*0.12-text_h/2",
                "center" => "(h-text_h)/2",
                _ => BottomOverlayY(spec, invariant)
            };
            var color = overlay.Color.StartsWith('#') ? "0x" + overlay.Color[1..] : overlay.Color.ToLowerInvariant();
            videoFilters.Add($"drawtext=fontfile='{EscapeFilterPath(overlayFontPath!)}':textfile='{EscapeFilterPath(overlay.TextFilePath)}':x=(w-text_w)/2:y={y}:fontsize={overlay.FontSize}:fontcolor={color}:borderw=3:bordercolor=black@0.75:enable='between(t,{overlay.StartSeconds.ToString("0.###", invariant)},{overlay.EndSeconds.ToString("0.###", invariant)})'");
        }

        // Every branch ends here. An RGBA logo dragged the overlay output up to 4:4:4, which
        // encodes to a High 4:4:4 Predictive stream that phones cannot hardware-decode and the
        // platforms re-encode or reject - and whose full-size chroma planes roughly double what
        // x264 has to allocate per frame. setsar keeps the pixels square: scaling a cropped
        // window leaves a fractional SAR behind, which players honour as a slight stretch.
        const string deliveryFormat = "format=yuv420p,setsar=1";
        var graph = new List<string>();
        // The picture is built once, then each card is laid over it in turn, then the logo, then
        // the delivery format closes the chain. Chaining rather than special-casing keeps the
        // one rule that matters: branding goes on last.
        var stage = "vbase";
        if (cardInputs.Count > 0 || logoInput is not null)
            graph.Add($"[0:v]{string.Join(',', videoFilters)}[vbase]");

        for (var index = 0; index < cardInputs.Count; index++)
        {
            var (input, overlay) = cardInputs[index];
            var y = overlay.YFraction is { } fraction
                ? $"H*{fraction.ToString("0.####", invariant)}"
                : overlay.Position.ToLowerInvariant() switch
                {
                    "top" => "H*0.06",
                    "center" => "(H-h)/2",
                    _ => $"H-h-H*0.10"
                };
            var next = $"vcard{index}";
            graph.Add($"[{input}:v]format=rgba[card{index}]");
            graph.Add($"[{stage}][card{index}]overlay=(W-w)/2:{y}:format=auto:" +
                      $"enable='between(t,{overlay.StartSeconds.ToString("0.###", invariant)},{overlay.EndSeconds.ToString("0.###", invariant)})'[{next}]");
            stage = next;
        }

        if (cardInputs.Count == 0 && logoInput is null)
            graph.Add($"[0:v]{string.Join(',', videoFilters)},{deliveryFormat}[vout]");
        else if (logoInput is null)
            graph.Add($"[{stage}]{deliveryFormat}[vout]");
        else
        {
            // Branding goes on last so it sits over the hook and the captions - the brief allows
            // exactly one watermark, and a mark something else can cover is not a watermark.
            var brand = spec.Logo!;
            var logoWidth = Math.Max(2, (int)Math.Round(spec.Width * brand.WidthFraction / 2) * 2);
            var logoFilters = new List<string>();
            if (brand.CropRect is not null) logoFilters.Add($"crop={brand.CropRect}");
            logoFilters.Add($"scale={logoWidth}:-1");
            logoFilters.Add("format=rgba");
            if (brand.Opacity < 1) logoFilters.Add($"colorchannelmixer=aa={brand.Opacity.ToString("0.###", invariant)}");
            var margin = brand.MarginFraction.ToString("0.####", invariant);
            var y = brand.Position.ToLowerInvariant() switch
            {
                "top" => $"H*{margin}",
                "center" => "(H-h)/2",
                _ => $"H-h-H*{margin}"
            };
            graph.Add($"[{logoInput.Value}:v]{string.Join(',', logoFilters)}[vlogo]");
            graph.Add($"[{stage}][vlogo]overlay=(W-w)/2:{y}:format=auto,{deliveryFormat}[vout]");
        }
        var voiceFilters = $"atrim=0:{durationText},asetpts=PTS-STARTPTS";
        graph.Add(musicInput.HasValue
            ? $"[0:a]{voiceFilters},asplit=2[voice][voicekey]"
            : $"[0:a]{voiceFilters}[voice]");
        var mixInputs = new List<string> { "[voice]" };
        if (musicInput.HasValue)
        {
            var ratio = Math.Clamp(Math.Abs(spec.MusicDuckingDb) / 2d, 2d, 20d).ToString("0.##", invariant);
            graph.Add($"[{musicInput.Value}:a]atrim=0:{durationText},asetpts=PTS-STARTPTS,volume={spec.MusicVolume.ToString("0.###", invariant)}[musicbase]");
            graph.Add($"[musicbase][voicekey]sidechaincompress=threshold=0.05:ratio={ratio}:attack=20:release=500[musicducked]");
            mixInputs.Add("[musicducked]");
        }
        for (var index = 0; index < soundEffectInputs.Count; index++)
        {
            var (input, effect) = soundEffectInputs[index];
            var delayMs = Math.Round(effect.AtSeconds * 1000).ToString("0", invariant);
            graph.Add($"[{input}:a]adelay={delayMs}:all=1,volume={effect.GainDb.ToString("0.###", invariant)}dB,atrim=0:{durationText}[sfx{index}]");
            mixInputs.Add($"[sfx{index}]");
        }
        var audioTail = spec.NormalizeAudio ? ",loudnorm=I=-14:TP=-1.5:LRA=11" : string.Empty;
        if (mixInputs.Count == 1)
            graph.Add($"[voice]{(spec.NormalizeAudio ? "loudnorm=I=-14:TP=-1.5:LRA=11" : "anull")}[aout]");
        else
            // amix scales every input by 1/inputs, but it scales them all equally and loudnorm
            // renormalises the sum afterwards, so the music-to-voice balance is unaffected by
            // how many sound effects a clip carries. Music level is set by MusicVolume alone.
            graph.Add($"{string.Concat(mixInputs)}amix=inputs={mixInputs.Count}:duration=first:dropout_transition=2{audioTail}[aout]");

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]", "-map", "[aout]",
            "-t", durationText, "-c:v", "libx264", "-preset", "medium", "-crf", spec.Crf.ToString(invariant)]);
        if (spec.EncoderThreads > 0) args.AddRange(["-threads", spec.EncoderThreads.ToString(invariant)]);
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", spec.OutputPath]);
        return args;
    }

    /// <summary>
    /// Crops in source pixels around the tracked subject, then scales to the output. The x
    /// position is a sum of clamped ramps - one per keyframe gap - which interpolates linearly
    /// between keyframes without the nesting an if-chain would need. Commas inside the
    /// expression are escaped so the filtergraph parser does not read them as filter breaks.
    /// </summary>
    private static IEnumerable<string> BuildTrackedCrop(FfmpegSubjectTrack track, int outputWidth,
        int outputHeight, System.Globalization.CultureInfo invariant)
    {
        var cropWidth = Even(Math.Min(track.SourceWidth, track.SourceHeight * (double)outputWidth / outputHeight));
        var cropHeight = Even(Math.Min(track.SourceHeight, track.SourceWidth * (double)outputHeight / outputWidth));
        var frames = track.Keyframes.OrderBy(x => x.AtSeconds).ToList();

        var expression = new StringBuilder(frames[0].X.ToString("0.##", invariant));
        for (var index = 1; index < frames.Count; index++)
        {
            var span = frames[index].AtSeconds - frames[index - 1].AtSeconds;
            if (span <= 0) continue;
            var slope = (frames[index].X - frames[index - 1].X) / span;
            if (Math.Abs(slope) < 0.01) continue;
            var from = frames[index - 1].AtSeconds.ToString("0.###", invariant);
            expression.Append($"+{slope.ToString("0.###", invariant)}*clip(t-{from}\\,0\\,{span.ToString("0.###", invariant)})");
        }

        yield return $"crop={cropWidth}:{cropHeight}:'clip({expression}\\,0\\,in_w-out_w)':(in_h-out_h)/2";
        yield return $"scale={outputWidth}:{outputHeight}";

        static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
    }

    /// <summary>
    /// Where bottom-anchored text sits. The branding logo is anchored to the same edge, so text
    /// at the usual 85% lands straight on top of it - and the CTA overlay is always bottom, so
    /// any campaign with a logo collided. When a bottom logo is present the text is lifted clear
    /// of the logo band instead.
    /// </summary>
    private static string BottomOverlayY(FfmpegRenderSpec spec, System.Globalization.CultureInfo invariant)
    {
        if (spec.Logo is not { } logo || !logo.Position.Equals("Bottom", StringComparison.OrdinalIgnoreCase))
            return "h*0.85-text_h/2";

        var logoWidth = Math.Max(2, (int)Math.Round(spec.Width * logo.WidthFraction / 2) * 2);
        // The crop rectangle carries the artwork's aspect ratio, which is the only way to know
        // how tall the scaled logo lands before FFmpeg resolves its -1 height.
        var ratio = 0.25;
        if (logo.CropRect is { } rect)
        {
            var parts = rect.Split(':');
            if (parts.Length == 4 &&
                double.TryParse(parts[0], System.Globalization.NumberStyles.Any, invariant, out var cropWidth) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Any, invariant, out var cropHeight) &&
                cropWidth > 0)
                ratio = cropHeight / cropWidth;
        }
        var logoHeight = (int)Math.Round(logoWidth * ratio);
        var margin = logo.MarginFraction.ToString("0.####", invariant);
        return $"h-h*{margin}-{logoHeight.ToString(invariant)}-text_h-24";
    }

    private static bool IsSafeColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value[0] == '#') return value.Length is 7 or 9 && value[1..].All(Uri.IsHexDigit);
        return value.All(char.IsLetter) && value.Length <= 20;
    }

    private static string ResolveOverlayFontPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The configured FFmpeg overlay font file does not exist.", fullPath);
            return fullPath;
        }

        var candidates = new List<string>();
        var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            candidates.Add(Path.Combine(windowsDirectory, "Fonts", "arial.ttf"));
            candidates.Add(Path.Combine(windowsDirectory, "Fonts", "segoeui.ttf"));
        }
        candidates.AddRange([
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf"
        ]);
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No usable overlay font was found. Configure WhopClipper:OverlayFontPath with a .ttf or .otf file.");
    }

    private static string EscapeFilterPath(string value) => Path.GetFullPath(value).Replace('\\', '/')
        .Replace(":", "\\:", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);

    [GeneratedRegex(@"^\d{1,5}:\d{1,5}:\d{1,5}:\d{1,5}$")]
    private static partial Regex CropRectPattern();
}

/// <summary>
/// Which clock a subtitle file's timestamps are on. Captions built per clip start at zero;
/// a track downloaded with the source is stamped against the whole video.
/// </summary>
public enum SubtitleTimeBase { ClipRelative, SourceAbsolute }

/// <summary>
/// A branding image burned over every frame. Sizes and margins are fractions of the output
/// frame so the same logo lands in the same place whatever the platform's resolution is.
/// <paramref name="CropRect"/> is an FFmpeg "w:h:x:y" rectangle trimming the transparent
/// padding off a full-frame logo sheet; null uses the image as-is.
/// </summary>
public sealed record FfmpegLogoOverlay(string Path, string Position = "Bottom", double WidthFraction = 0.45,
    double MarginFraction = 0.14, double Opacity = 1, string? CropRect = null);

/// <summary>
/// Where the subject sits over the clip, in source pixels: <paramref name="Keyframes"/> give the
/// left edge of the crop window at a time offset from the clip start, interpolated in between.
/// </summary>
public sealed record FfmpegSubjectTrack(int SourceWidth, int SourceHeight,
    IReadOnlyList<CropKeyframe> Keyframes);

/// <summary>
/// A short full-frame colour wash on a reaction beat. Measured across sixteen reference shorts
/// these run 0.4-0.7s and usually land with a sound effect, which is why they are timed
/// separately from the zoom punches rather than bolted onto them.
/// </summary>
public sealed record FfmpegColorFlash(double AtSeconds, double DurationSeconds, double Intensity = 0.45,
    string Color = "Green");

/// <summary>
/// A punch-in on a beat: the frame ramps from its normal scale up to <paramref name="Scale"/>
/// across the window and snaps back after. Reference clips run 1.1x to 2.3x over 0.3-1.0s.
/// </summary>
public sealed record FfmpegZoomPunch(double AtSeconds, double DurationSeconds, double Scale = 1.25);

public sealed record FfmpegSoundEffectInput(string Path, double AtSeconds, double GainDb);
/// <param name="CardImagePath">When set, the hook is composited from this transparent PNG
/// instead of being drawn with drawtext, which cannot round a box's corners.</param>
/// <param name="YFraction">Top edge as a fraction of frame height, overriding Position.</param>
public sealed record FfmpegOverlayInput(string TextFilePath, double StartSeconds, double EndSeconds,
    string Position, int FontSize, string Color, string? CardImagePath = null, double? YFraction = null);
public sealed record FfmpegRenderSpec(string SourcePath, string OutputPath, double StartSeconds, double EndSeconds,
    int Width, int Height, int FramesPerSecond, int Crf, bool NormalizeAudio, string Framing,
    string? SubtitlePath, string? SubtitleStyle, string? MusicPath, double MusicVolume, double MusicDuckingDb,
    IReadOnlyList<FfmpegSoundEffectInput> SoundEffects, IReadOnlyList<FfmpegOverlayInput> Overlays,
    string? OverlayFontPath = null, SubtitleTimeBase SubtitleTimeBase = SubtitleTimeBase.ClipRelative,
    FfmpegLogoOverlay? Logo = null, FfmpegSubjectTrack? SubjectTrack = null, int EncoderThreads = 0,
    IReadOnlyList<FfmpegColorFlash>? ColorFlashes = null, IReadOnlyList<FfmpegZoomPunch>? ZoomPunches = null);
