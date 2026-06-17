# Mathom

> *A mathom is anything Hobbits had no immediate use for, but were unwilling to throw away.*

A self-hosted "second brain": capture ideas (voice, text, photo) with near-zero effort, and have
them automatically cleaned, classified, embedded, and made searchable.

## Status

Early development. Walking skeleton implemented (text capture → async LLM cleanup/classify →
timeline + full-text search). The capture foundation is designed; see
[`docs/superpowers/specs/2026-06-17-mathom-capture-foundation-design.md`](docs/superpowers/specs/2026-06-17-mathom-capture-foundation-design.md).

## Run with Docker Compose

```bash
cp .env.example .env       # then edit: set an OpenRouter API key (and a strong Postgres password)
docker compose up --build  # builds the web image, starts Postgres, auto-applies migrations
```

Then open <http://localhost:8080> — `/Capture` to dump a note, `/` for the timeline + search.

- Config is supplied via `.env` (gitignored). ASP.NET Core reads nested keys using `__`, e.g.
  `Llm__OpenRouter__ApiKey`. See [`.env.example`](.env.example).
- OpenRouter alone is enough to start: the Infomaniak primary fails fast on an empty model and the
  client falls back to OpenRouter. Without any provider key, capture and browsing still work but
  items land in `Failed` status (cleanup has no provider).
- The schema is applied automatically on startup; no manual `dotnet ef database update` needed.

### Run locally without Docker

Needs a local PostgreSQL matching `ConnectionStrings:Mathom` in `appsettings.json` (or override it),
then `dotnet run --project src/Mathom.Web`. Put provider keys in `appsettings.Development.json`
(gitignored).

## PWA & offline

Mathom is an installable PWA that opens to the capture screen and works offline.

- **Install:** open the app in a browser and use "Install app" / "Add to Home Screen".
  The installed app launches straight to `/Capture`.
- **Offline capture:** if you capture while offline, the note (text or voice audio) is
  queued in the browser (IndexedDB) and shown as "Saved offline — will sync when you're
  back." It replays automatically when connectivity returns (on reconnect, when the tab
  becomes visible, or on next load). Replays are idempotent, so nothing is duplicated.
- **Browsing/search are online-only** — only capture works offline.
- **HTTPS is required for install + service worker.** `localhost` is exempt (so local dev
  works), but installing on your phone over the tailnet needs HTTPS — serve it via
  `tailscale serve` (or `tailscale cert` + your reverse proxy) so the `*.ts.net` host has
  a valid certificate. Plain-HTTP over the tailnet will load but won't install or go offline.
- **iOS note:** background replay is unreliable on iOS Safari, so Mathom replays the queue
  whenever the app is foregrounded or reconnects, which works across iOS, Android, and desktop.

## Stack

ASP.NET Core (Razor Pages) · HTMX · Alpine.js · PostgreSQL + pgvector · PWA (offline-capable
capture). Self-hosted on Hetzner, reachable over Tailscale. AI via Infomaniak (primary) and
OpenRouter (fallback).

## Roadmap

- **v1 (current):** frictionless capture, async processing pipeline, hybrid search, related items.
- **Later:** MCP server for Claude Code access; autonomous "work on this idea" background runs;
  LLM-built knowledge graph.
