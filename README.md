# Mathom

> *A mathom is anything Hobbits had no immediate use for, but were unwilling to throw away.*

A self-hosted "second brain": capture a thought by voice or text with near-zero friction, and have
it automatically transcribed, cleaned up, classified, and tagged by an LLM — then browse, search, and
refine it. Designed to run for you (and a few invited people) on your own server.

> **Status:** actively developed and self-hostable. Single- or small-multi-user, with admin-gated
> sign-up. Not yet hardened for public/untrusted users. See [Roadmap](#roadmap) and
> [Towards open source](#towards-open-source).

---

## How it works

Capture is instant and cheap; the slow work (transcription, LLM cleanup) happens **asynchronously** in
the background, so the UI never blocks. A note moves through these stages:

```
                 capture (text or voice)
                          │
                          ▼
                  Item (status: Pending)         ← saved immediately, shown on the timeline as in-flight
                          │
          ProcessingWorker claims it             ← background loop, FOR UPDATE SKIP LOCKED (safe to scale)
                          │
                          ▼
              ItemProcessor.ProcessAsync
                ├─ if voice: transcribe audio (Whisper)      ┐
                ├─ LLM cleanup with your glossary as context │  Infomaniak (primary) → OpenRouter (fallback)
                │   → title, clean text, type, actionable,   │
                │     tags                                    ┘
                └─ deterministic glossary correction (variant → term)
                          │
                          ▼
                  Item (status: Ready)           ← timeline/search/detail update; poller settles the UI
```

Everything is **scoped per user** (EF Core global query filters on `UserId`), and access is **gated by
admin approval** — a new account can sign in but is held on a pending page until an admin approves it.

### The glossary (the interesting bit)

Speech-to-text and LLMs mangle domain-specific words ("Obersaxen" → "Obersachsen", "FireSkills" →
"Fairstills"). Mathom lets each user build a personal **glossary** that feeds the pipeline:

- **Terms** — correct spellings, injected into both the Whisper prompt and the LLM cleanup prompt.
- **Variants** — known mis-hearings. Captured with a **select-to-add** gesture: highlight the wrong
  word in a note, type the correction, done. A pure, deterministic corrector then whole-word-replaces
  the variant with the canonical term in the cleaned text/title/tags (the raw transcript is left
  untouched), and the variants are also given to the LLM as hints.
- **Descriptions** — optional domain context per term (e.g. "FireSkills — our internal time-tracking
  product"), injected into the cleanup prompt so the model picks sharper titles/types/tags. Manually
  curated (the LLM reads them, never rewrites them).
- **Re-process** — re-run cleanup on an existing note (button, or offered right after you add a term)
  so corrections apply retroactively, without re-transcribing.

---

## Features

**Implemented**

- **Frictionless capture** — text and voice (audio), installable as a PWA that opens straight to the
  capture screen.
- **Offline capture** — capture works offline; notes queue in the browser (IndexedDB) and replay
  idempotently when you reconnect (browsing/search are online-only).
- **Async processing pipeline** — a background worker transcribes (Whisper) and runs LLM cleanup
  (title, clean text, item type, actionable flag, tags), with provider fallback.
- **Timeline + full-text search** — Postgres full-text search (`tsvector` over title + clean text,
  GIN-indexed), with type / actionable / tag filters; clickable tags.
- **Notes** — detail view (with the raw transcript), inline edit, soft-delete to trash, restore/purge.
- **Per-user glossary** — terms, variants (select-to-add), descriptions, deterministic correction,
  re-process (see above).
- **Accounts & access** — ASP.NET Core Identity (cookie auth), strict per-user data isolation, and
  **admin approval**: new users are gated until an admin approves them at `/Admin/Users`.

**Roadmap**

- **Photo capture + OCR** — capture an image, extract text.
- **Semantic search** — embeddings / vector search and "related notes" (today's search is keyword/FTS
  only; there is no pgvector yet).
- **LLM-assisted glossary enrichment** — a "suggest a description" action that drafts term context from
  the notes it appears in, for you to accept/edit (human-in-the-loop, never auto-write).
- **Export / backup** — get your notes out as Markdown / JSON.
- **MCP server** — expose your notes to Claude Code / other agents.
- **Longer horizon** — autonomous "work on this idea" background runs; an LLM-built knowledge graph.

---

## Tech stack

- **ASP.NET Core (Razor Pages)** on **.NET 10**, **HTMX** + a little vanilla JS / Alpine.js for
  interactivity, server-rendered partials for live updates.
- **PostgreSQL** via **EF Core 10 / Npgsql**; full-text search via generated `tsvector` columns.
- **ASP.NET Core Identity** (cookie auth, roles) for accounts and the admin area.
- **PWA** — service worker + IndexedDB for offline-capable capture.
- **AI** — Infomaniak (primary) with OpenRouter (fallback) for LLM cleanup; Infomaniak Whisper for
  speech-to-text. Providers are pluggable behind `ILlmClient` / `ITranscriber`.
- **Deploy** — Docker Compose (web + Postgres). Intended to run on a small server, reachable over
  Tailscale; Data Protection keys are persisted to a volume so auth cookies survive container
  rebuilds.

---

## Getting started

You need Docker (and [`just`](https://github.com/casey/just), optional but handy).

```bash
cp .env.example .env        # then edit — see Configuration below
just up                     # build + start Postgres and the web app (auto-applies migrations)
# or without just:  docker compose up --build -d
```

Open <http://localhost:8080> — **register the first account using the email you set as `AdminEmail`**;
it is auto-approved and made an admin. `/Capture` adds a note, `/` is the timeline, `/Glossary`
manages your glossary, `/Admin/Users` approves new users.

Handy `just` recipes: `just logs web`, `just ps`, `just restart` (after editing `.env`),
`just reset` (⚠️ stops the stack **and wipes the database volume** — a clean slate),
`just psql` (a shell on the DB).

### Run locally without Docker

Needs the **.NET 10 SDK** and a local PostgreSQL matching `ConnectionStrings:Mathom` in
`appsettings.json` (or override it). Then `dotnet run --project src/Mathom.Web`. Put provider keys and
`AdminEmail` in `appsettings.Development.json` (gitignored).

### Tests

`just test` (or `dotnet test`). Integration tests spin up a real PostgreSQL via
[Testcontainers](https://testcontainers.com/), so Docker must be running. ~117 tests covering the
pipeline, per-user isolation, the glossary, and the approval gate.

---

## Configuration

All config is supplied via `.env` (gitignored; loaded by the `web` service). ASP.NET Core reads
**nested** keys from env vars using `__` as the separator. See [`.env.example`](.env.example).

| Key | What it does |
| --- | --- |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | Database credentials (also compose the app's connection string). |
| `WEB_PORT` | Host port for the web app (container always listens on 8080). |
| `AdminEmail` | The owner's login email. The user who registers with it (or an existing user with it, on startup) is approved + made an **admin**. **Set this before first run** or no one can approve users. |
| `Llm__OpenRouter__ApiKey` / `Llm__OpenRouter__Model` | OpenRouter (fallback LLM). Enough on its own to get started. |
| `Llm__Infomaniak__*` | Infomaniak (primary LLM). Optional; leave blank to use OpenRouter only. |
| `Stt__Infomaniak__*` | Infomaniak Whisper for voice transcription. Set to enable voice capture. |

Without any LLM provider key, capture and browsing still work, but notes land in `Failed` status
(cleanup has no provider). The schema is applied automatically on startup — no manual `dotnet ef`.

> **Migrations are additive only.** The production database is a persistent volume; never regenerate
> the initial migration. New schema goes in a new additive migration.

---

## PWA & offline

Mathom is an installable PWA that opens to the capture screen and works offline.

- **Install:** open the app and use "Install app" / "Add to Home Screen"; the installed app launches to
  `/Capture`.
- **Offline capture:** capturing while offline queues the note (text or audio) in the browser
  (IndexedDB) and shows "Saved offline — will sync." It replays automatically and idempotently on
  reconnect, when the tab becomes visible, or on next load. Browsing/search are online-only.
- **HTTPS is required** for install + service worker (`localhost` is exempt). Over a tailnet, serve via
  `tailscale serve` (or `tailscale cert` + a reverse proxy) so the `*.ts.net` host has a valid cert.
- **iOS note:** background replay is unreliable on iOS Safari, so the queue replays on foreground /
  reconnect — which works across iOS, Android, and desktop.

---

## Project layout

```
src/Mathom.Web/
  Capture/        text & voice capture endpoints
  Processing/     ProcessingWorker, ItemProcessor, LLM clients, transcriber, prompt builder
  Glossary/       GlossaryService, GlossaryCorrector
  Notes/          NoteService (edit / delete / trash / reprocess)
  Search/         SearchService (timeline, full-text search, filters)
  Admin/          UserAdminService, AdminBootstrap (admin role + approval)
  Auth/           ApprovalGateMiddleware
  Domain/         entities (Item, Tag, GlossaryTerm/Variant, ApplicationUser, …)
  Data/           MathomDbContext + EF migrations (additive)
  Pages/          Razor Pages + shared partials
  wwwroot/        PWA assets, service worker, CSS, JS
tests/Mathom.Tests/   xUnit + Testcontainers integration tests
docs/superpowers/     design specs & implementation plans
```

---

## Towards open source

This repo is being prepared for open source. Still to do before flipping it public:

- [x] **Choose a license** — MIT (see [`LICENSE`](LICENSE)).
- [ ] **Scan the git history for secrets** — API keys live in `.env` (gitignored) today, but confirm
      none were ever committed; rotate anything that was.
- [ ] Add `CONTRIBUTING.md`, a code of conduct, and issue/PR templates.
- [ ] A short architecture/design doc beyond this README (the `docs/` specs are a good seed).

## License

[MIT](LICENSE) © 2026 Marco Wettstein.
