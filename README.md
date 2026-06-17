# Mathom

> *A mathom is anything Hobbits had no immediate use for, but were unwilling to throw away.*

A self-hosted "second brain": capture ideas (voice, text, photo) with near-zero effort, and have
them automatically cleaned, classified, embedded, and made searchable.

## Status

Early development. The capture foundation is designed; see
[`docs/superpowers/specs/2026-06-17-mathom-capture-foundation-design.md`](docs/superpowers/specs/2026-06-17-mathom-capture-foundation-design.md).

## Stack

ASP.NET Core (Razor Pages) · HTMX · Alpine.js · PostgreSQL + pgvector · PWA (offline-capable
capture). Self-hosted on Hetzner, reachable over Tailscale. AI via Infomaniak (primary) and
OpenRouter (fallback).

## Roadmap

- **v1 (current):** frictionless capture, async processing pipeline, hybrid search, related items.
- **Later:** MCP server for Claude Code access; autonomous "work on this idea" background runs;
  LLM-built knowledge graph.
