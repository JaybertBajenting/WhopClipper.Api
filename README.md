# Whop Clipping Assistant API

Local ASP.NET Core 9 backend for the Whop clipping workflow. It persists campaigns in PostgreSQL, keeps media on disk, queues long-running local-tool jobs, and can serve the compiled React application as one desktop-friendly origin.

## Implemented MVP capabilities

- Campaign create/read/update/archive/duplicate, searchable list, dashboard summary.
- Versioned heuristic requirement extraction, correction, approval, and immutable approved revisions.
- TikTok, Instagram Reels, YouTube Shorts, and X platform profiles.
- Source URL registration, bounded `yt-dlp` inspection, video downloads, and available English/manual/automatic SRT downloads.
- Claude CLI, Codex CLI, Auto, and Compare analysis modes behind `IAiMediaProvider`.
- Schema-bound highlight parsing and persisted ranked highlight candidates.
- Typed edit plans and deterministic FFmpeg render arguments for crop/fit framing, rebased subtitle burn-in,
  background music with voice ducking, timestamped SFX, CTA/text overlays, and loudness normalization.
- FFprobe output checks for duration and dimensions.
- Local music/SFX location indexing, hashes, tags, favorites, and license notes.
- Persisted queued/running/completed/failed/cancelled job state, retry, and cancellation.
- Export of publishing copy and compliance items to `publishing-package.md`.
- Swagger, Serilog file/console logs, liveness/readiness checks, restricted CORS, path containment, and loopback defaults.
- Production static-file hosting with SPA fallback. Unknown `/api`, `/health`, and `/swagger` paths remain real 404 responses.

## Prerequisites

- .NET 9 SDK
- PostgreSQL 14+
- FFmpeg and FFprobe
- `yt-dlp`
- Optional: authenticated Claude CLI and/or Codex CLI

Once a PostgreSQL connection string is configured, the API still starts if PostgreSQL or media/AI executables are unavailable. `/health` reports process liveness, `/health/ready` reports connection and migration readiness, and `GET /api/system/dependencies` reports PostgreSQL plus local executable status. Database-backed routes require PostgreSQL.

## Configuration

Copy `.env.example` into your preferred local environment loader or set the variables in PowerShell. ASP.NET Core uses double underscores for nested keys.

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings__Postgres` | local `whop_clipper` database | PostgreSQL connection |
| `WhopClipper__WorkspaceRoot` | `./workspace` | Media/output root |
| `WhopClipper__YtDlpPath` | `yt-dlp` | Downloader executable |
| `WhopClipper__FfmpegPath` | `ffmpeg` | Renderer executable |
| `WhopClipper__FfprobePath` | `ffprobe` | Probe executable |
| `WhopClipper__ClaudePath` | `claude` | Claude CLI executable |
| `WhopClipper__CodexPath` | `codex` | Codex CLI executable |
| `WhopClipper__OverlayFontPath` | Windows Arial/Segoe UI fallback | Explicit `.ttf`/`.otf` file used by FFmpeg overlays |
| `WhopClipper__AutoMigrate` | `true` | Apply EF migrations at startup |
| `WhopClipper__AllowedOrigins__0` | `http://localhost:5173` | Development frontend origin |
| `ASPNETCORE_URLS` | launch profile uses `http://localhost:5080` | Backend/desktop origin |

Executable paths are operator configuration, not client-provided command text. CLI credentials remain in each provider's existing credential store and are not saved in PostgreSQL.
Settings saved through the UI are written atomically to `%LOCALAPPDATA%\WhopClipper\appsettings.Local.json` and take effect after restarting the backend.

## Run locally

From `BackendGame`:

```powershell
dotnet tool restore --configfile NuGet.Config
dotnet restore WhopClipper.slnx --configfile NuGet.Config
dotnet tool run dotnet-ef database update --project src/WhopClipper.Api
dotnet run --project src/WhopClipper.Api
```

Open:

- API: `http://localhost:5080`
- Swagger: `http://localhost:5080/swagger`
- Liveness: `http://localhost:5080/health`
- Database readiness: `http://localhost:5080/health/ready`

The solution filename is retained for compatibility with the original scaffold. The produced backend assembly and executable are `WhopClipper.Api`.

## React SPA and desktop publish

For the packaged desktop build, compile `FrontendGame` and copy the **contents** of `FrontendGame/dist` to `src/WhopClipper.Api/wwwroot` before `dotnet publish`. The Web SDK includes `wwwroot` automatically. The WPF launcher can set a free loopback URL through `ASPNETCORE_URLS`, launch `WhopClipper.Api.exe` hidden, poll `/health`, and navigate its WebView to the same origin.

Development can continue to run Vite at `http://localhost:5173`; CORS allows that origin by default.

## Important endpoints

The full surface is in Swagger. Main route groups are:

- `/api/dashboard`
- `/api/campaigns`
- `/api/campaigns/{id}/extract-requirements`
- `/api/campaigns/{id}/requirement-revisions/{revisionId}`
- `/api/campaigns/{id}/platform-profiles/{platform}`
- `/api/campaigns/{id}/sources`, `/downloads`, `/ai-analysis`, `/edit-plans`, `/exports`
- `/api/ai-analysis/{id}`
- `/api/campaigns/{id}/ai-analysis`, `/api/campaigns/{id}/ai-analysis/latest`
- `/api/edit-plans/{id}/renders`, `/api/renders/{jobId}/validate`
- `/api/jobs`
- `/api/media-library`
- `/api/settings`, `/api/system/dependencies`

Enums are serialized as strings. Job states are `Queued`, `Running`, `Completed`, `Failed`, and `Cancelled`.

## Verification

```powershell
dotnet build WhopClipper.slnx -c Release
dotnet test WhopClipper.slnx -c Release
```

The test suite covers requirement extraction, defaults/review flags, workspace traversal defense, filename sanitization,
typed FFmpeg graphs, subtitle time rebasing, CLI isolation flags, and strict Claude/Codex structured-output parsing.

## Current MVP boundaries

- Joining Whop campaigns, publishing, and Whop submissions are intentionally manual.
- Requirement extraction is deterministic/heuristic. AI-backed extraction can be added behind the existing interface.
- AI analysis requires either a supplied transcript or the stored subtitle downloaded with the source. Speech-to-text,
  diarization, scene detection, and contact-sheet creation are future pipeline stages.
- Framing is deterministic center crop or fit/letterbox; semantic subject tracking/dynamic reframing is not implemented.
- AI CLI output formats/flags must be compatibility-tested after CLI upgrades. Compare mode preserves a successful
  provider if the other fails; Auto deterministically prefers available Codex, then Claude, without runtime fallback.
- Media progress is persisted at lifecycle milestones rather than parsed frame-by-frame from each CLI.
- Settings changes are persisted but require restart to affect injected executable/workspace options.
- Export writes the text/checklist package alongside rendered output; it does not open Explorer or publish externally.
