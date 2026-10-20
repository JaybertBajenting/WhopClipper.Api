# CLAUDE.md — WhopClipper.Api

Backend for WhopClipper, a local Windows app that turns long videos (streams, podcasts, vlogs) into
platform-ready vertical clips. ASP.NET Core minimal API on .NET 9, PostgreSQL through EF Core, a
background job queue, and adapters around FFmpeg, yt-dlp, faster-whisper and the Claude / Codex CLIs.

The React front end (`WhopClipper.Web`) builds into `src/WhopClipper.Api/wwwroot`; a WPF/WebView2 host
packages both into one desktop app. This repository is only the API.

## Commands

```powershell
dotnet build WhopClipper.slnx -c Release
dotnet test  WhopClipper.slnx -c Release          # xUnit, ~530 tests, runs in seconds
dotnet run --project src/WhopClipper.Api           # needs PostgreSQL + a .env (copy .env.example)
dotnet tool restore; dotnet ef migrations add <Name> --project src/WhopClipper.Api
```

Run the tests after every change. A build with only the pre-existing nullable warnings is normal.

## Layout

```
src/WhopClipper.Api/
  Program.cs                 composition root, options, Serilog, static SPA hosting
  App/Contracts/             request/response records and FluentValidation validators
  App/Domain/                EF entities (Campaign, SourceAsset, EditPlan, JobRecord, ...)
  App/Data/                  AppDbContext + migrations (schema "clipper")
  App/Endpoints/             minimal-API route groups, one file per area
  App/Services/              jobs, pipelines, FFmpeg graph builders, AI/CLI adapters
  App/Services/*.py          Python helpers run as subprocesses (transcribe, framing, b-roll, narrated)
tests/WhopClipper.Api.Tests/Tests/   one test class per feature area
```

`JobExecutionService` is a large partial class split by pipeline: `Jobs.cs` (dispatch, download,
transcribe, render), `Jobs.PodcastSplit.cs`, `Jobs.Narrated.cs`, `Jobs.CarScenes.cs`, `Jobs.BrollRepair.cs`.
FFmpeg command lines are built by `FfmpegArgumentBuilder` partials (`FfmpegNarrated.cs`,
`FfmpegPodcastSplit.cs`, ...) as argument lists; there is no shell in between.

## How a run works

1. A source (YouTube URL, stream VOD, or local file) is added to a campaign.
2. A `SourcePipeline` job resolves the campaign's edit style (`Streamer`, `PodcastSplit`, `NarratedLuxury`)
   and branches. Transcripts come from YouTube captions when they exist, otherwise faster-whisper.
3. The AI is called through the CLI with a JSON schema (`RunStructuredAiAsync`); everything from the
   video or the campaign goes inside an `UNTRUSTED_CAMPAIGN_DATA` block and is treated as data.
4. Replies are parsed and validated in C# (`StoryParser`, `NarratedStories.ParseMoments`, ...). The
   model's choices are never trusted blindly: pieces are snapped to real word timings, overlaps with
   clips already made are rejected, and labels the transcript never says are filtered out.
5. An `EditPlan` is stored, then rendered by FFmpeg. Re-renders reuse the plan, so changing a song or a
   title costs no AI call.

## Conventions

- Comments explain *why*, and cite the measurement or the failure that motivated the code
  (e.g. "measured on 16 reference shorts", "vstack crashes the bundled ffmpeg"). Keep that style; do not
  add comments that restate the code.
- Tests are named as sentences describing behaviour (`NoShotPlaysTwiceWhileAnotherIsLeft`). Add a
  regression test with the real numbers from the bug that prompted a fix.
- Pure logic lives in static helpers so it can be unit tested without the database or a process.
- Write JSON with `new UTF8Encoding(false)` (no BOM). Use `JsonDefaults.Serializer` for camelCase.
- Every external process goes through `IProcessRunner` with an explicit `Timeout`.
- Paths from the database are workspace-relative; resolve with `WorkspaceService.ResolveRelative` and
  check untrusted paths with `WorkspaceService.EnsureInside`.

## Gotchas

- Windows only in practice. The bundled FFmpeg segfaults on `vstack`; compose with `pad` + `overlay`.
- yt-dlp section downloads must prefer HLS formats; YouTube DASH sections stall.
- Prompt wording is load-bearing and tuned against real output. Batch prompt changes and verify them on
  real videos rather than tweaking one rule at a time.
- Long-running jobs survive an app restart and resume; a render already running for a plan blocks a
  second render of the same plan (`Conflict`).

## Never

- Commit `.env`, OAuth client secrets, tokens, keystores or `google-services.json`. Config comes from
  `.env` and `appsettings*.json`; keep real values out of both in git.
- Put personal notes, client briefs or creator names in code, tests or commit messages: this repository
  is public.
- Build shell strings from user or AI input; always pass argument lists.
