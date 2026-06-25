# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Mathom is a self-hosted "second brain": capture a thought by voice or text, then an LLM transcribes,
cleans up, classifies, and tags it asynchronously. ASP.NET Core Razor Pages on .NET 10, HTMX +
server-rendered partials, PostgreSQL via EF Core, per-user data isolation with admin-gated sign-up.
See `README.md` for the user-facing overview and `CONTRIBUTING.md` for style expectations.

## Commands

```bash
just up            # build + start Postgres and web via Docker (auto-applies migrations); http://localhost:8080
just down          # stop (keeps DB volume)
just reset         # ⚠️ stop AND wipe the DB volume
just restart       # restart web only (e.g. after editing .env)
just logs web      # follow logs for one service
just psql          # psql shell on the running DB
just test          # run the full test suite (= dotnet test)
```

Run the app without Docker: `dotnet run --project src/Mathom.Web` (needs .NET 10 SDK + a local
Postgres matching `ConnectionStrings:Mathom`). Run a single test:
`dotnet test --filter "FullyQualifiedName~GlossaryCorrectorTests"`.

Tests require **Docker running** — integration tests spin up a real `postgres:17` via Testcontainers
(`tests/Mathom.Tests/PostgresFixture.cs`). There is no in-memory DB shortcut.

## Architecture

The data flow is **capture → async pipeline → ready**, decoupled by status on the `Item` entity:

1. **Capture** (`Capture/CaptureController.cs`, `Pages/Capture.cshtml`) writes an `Item` with
   `Status = Pending` immediately and returns — text or voice (audio is stored via `IMediaStore`).
   Capture is idempotent on `IdempotencyKey` (unique index) so the offline PWA queue can replay safely.
2. **`ProcessingWorker`** (`Processing/ProcessingWorker.cs`) is a `BackgroundService` that polls for
   pending items. It claims one with `SELECT ... FOR UPDATE SKIP LOCKED` so the design is safe to scale
   to multiple workers. On startup it resets orphaned `Processing` rows back to `Pending` (this reset is
   only safe because there is exactly one worker — revisit if you scale out).
3. **`ItemProcessor.ProcessAsync`** (`Processing/ItemProcessor.cs`) does the work: transcribe voice
   (Whisper) if needed → LLM cleanup (title, clean text, type, actionable, tags) → deterministic
   glossary correction → `Status = Ready`. Failures set `Status = Failed` with `Error`; the item can be
   re-processed.

**LLM/transcription providers are pluggable** behind `ILlmClient` / `ITranscriber`. `FallbackLlmClient`
wraps the providers in order (Infomaniak primary → OpenRouter fallback) — see the DI wiring in
`Program.cs`. In tests these are swapped for `FakeLlmClient` / `FakeTranscriber`.

**The glossary** (`Glossary/`) feeds the pipeline three ways per user: *terms* bias the Whisper prompt
and the LLM cleanup prompt; *variants* (known mis-hearings) drive `GlossaryCorrector` — a pure,
deterministic whole-word replacement applied to cleaned text/title/tags (never the raw transcript);
*descriptions* add domain context to the cleanup prompt. Re-process re-runs cleanup without
re-transcribing.

**Per-user isolation & access control** are enforced at two layers:
- EF Core **global query filters** scope `Item`/glossary rows to data ownership and hide soft-deleted
  rows (`DeletedAt == null`). Pipeline code that must see across users (the worker claiming items) calls
  `.IgnoreQueryFilters()` explicitly.
- **`ApprovalGateMiddleware`** (`Auth/`) redirects authenticated-but-unapproved users to `/Pending`.
  New accounts can sign in but stay gated until an admin approves them at `/Admin/Users`.
  `AdminBootstrap` (run from `Program.cs` on startup) promotes the `AdminEmail` user to the Admin role
  and auto-approves them.

Search (`Search/SearchService.cs`) uses a generated Postgres `tsvector` column over `Title + CleanText`
(GIN-indexed), configured in `Data/MathomDbContext.cs`.

## Conventions

- **Migrations are additive only.** The production DB is a persistent volume. Never regenerate the
  initial migration; add a new additive migration for schema changes. The schema is applied
  automatically on startup (`Database.Migrate()` in `Program.cs`), not via manual `dotnet ef`.
- File-scoped namespaces, `async` + `CancellationToken` throughout, EF Core for data access.
- **Primary constructors are the default.** Inject dependencies via a primary constructor and reference
  the parameters directly (`db`, not a `_db` field) — don't write a separate constructor that just
  assigns `private readonly` fields. Fall back to an explicit constructor only when it does real
  initialization work beyond field assignment (e.g. `OpenRouterImageReader` parses config and mutates
  its `HttpClient`).
- HTMX + server-rendered Razor partials (`Pages/Shared/_*.cshtml`) for interactivity — prefer returning
  a partial over client-side JS for live updates.
- Config comes from `.env` (gitignored) using `__` as the nested-key separator (e.g.
  `Llm__OpenRouter__ApiKey`). `appsettings.Development.json` (gitignored) for local `dotnet run`.
- The `Testing` environment (set by `TestWebAppFactory`) disables the background worker and startup
  migration so tests manage the schema and processing themselves.

## Security note

This repo has a security-hardening focus (recent commits enforce Cloudflare Authenticated Origin
Pulls). `Program.cs` handles forwarded headers / Secure cookies carefully: the standalone
`docker-compose.yml` is directly exposed and must NOT trust `X-Forwarded-*`, while the deploy compose
(behind Caddy) sets `ForwardedHeaders:Enabled=true`. Preserve these distinctions when touching auth,
cookies, or proxy handling.
