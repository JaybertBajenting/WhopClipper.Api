# Desk Detective API

ASP.NET Core 10 backend for the workstation guessing game. It owns player identity, round timing, answer validation, speed-based scoring, host controls, Supabase PostgreSQL access, private Supabase Storage image URLs, and SignalR update notifications.

## What the API protects

- Correct answers are never sent before the host reveals a round.
- Scores and timestamps are calculated by the server.
- Players receive a random private token when they join; only its SHA-256 hash is stored.
- Host endpoints require the `X-Host-Key` header.
- The Supabase service-role key stays on the backend and must never be placed in the React app.

## Requirements

- .NET 10 SDK
- A Supabase project
- The separate `FrontendGame` repository

## Supabase setup

1. Create a Supabase project and keep its database password safe.
2. In **Connect**, copy the **Session pooler** connection string on port `5432`. This is a good fit for a persistent DigitalOcean web service and works on IPv4. Replace `[YOUR-PASSWORD]`.
3. In **Storage**, create a private bucket named `workstations`.
4. In **Project Settings → API Keys**, copy the project URL and a server-side secret/service-role key. Never expose that key to the browser.
5. The API runs its EF Core migrations automatically when it starts.

The connection string can be Supabase's PostgreSQL URI or Npgsql's key/value format. For example:

```text
postgresql://postgres.PROJECT_REF:PASSWORD@aws-0-REGION.pooler.supabase.com:5432/postgres
```

## Local development

Copy `.env.example` values into your shell, user secrets, or `appsettings.Development.json`. Do not commit real secrets.

```powershell
dotnet restore
dotnet run --project src/WorkstationGame.Api
```

The local API is available at `http://localhost:5087`. Health check: `GET /health`.

To create another migration after changing the model:

```powershell
dotnet tool restore
dotnet ef migrations add MigrationName --project src/WorkstationGame.Api --startup-project src/WorkstationGame.Api
```

## Environment variables

| Variable | Required | Purpose |
| --- | --- | --- |
| `ConnectionStrings__GameDatabase` | Yes | Supabase PostgreSQL connection string |
| `Game__HostKey` | Yes | Long random password entered on the host screen |
| `Game__AllowedOrigins` | Yes | Frontend URL; comma-separate multiple origins |
| `Game__SupabaseUrl` | For uploads | `https://PROJECT_REF.supabase.co` |
| `Game__SupabaseServiceRoleKey` | For uploads | Backend-only Storage key |
| `Game__SupabaseStorageBucket` | No | Defaults to `workstations` |
| `ASPNETCORE_ENVIRONMENT` | Production | Set to `Production` on DigitalOcean |

Generate a host key with a password manager or another cryptographically secure generator. Use at least 32 random characters.

## Deploy to DigitalOcean App Platform

1. Create an App Platform app from this GitHub repository.
2. Choose **Web Service** and the repository's `Dockerfile`.
3. Use HTTP port `8080` and health-check path `/health`.
4. Add every variable from the table as encrypted environment variables. Do not use build-time variables for secrets.
5. Set `Game__AllowedOrigins` to the final frontend URL, such as `https://your-frontend.ondigitalocean.app`.
6. Deploy one instance initially. The answer-ranking lock is process-local; a single instance gives deterministic placement for this meeting-sized app.
7. After deployment, open the root URL and confirm it returns `Desk Detective API` with status `ready`.

SignalR uses WebSockets when available and falls back automatically. App Platform handles HTTPS; no separate Nginx package or configuration is needed.

## Scoring

| Correct-answer rank | Points |
| --- | ---: |
| First | 100 |
| Second | 75 |
| Third | 50 |
| Everyone else correct | 25 |
| Incorrect | 0 |

Only one answer per player per round is accepted. The API uses its own receive time, not the player's device clock.

## Tests

```powershell
dotnet test
```

