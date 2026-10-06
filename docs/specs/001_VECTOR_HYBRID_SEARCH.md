# 001 — Vector / Hybrid Search Foundation

**Spec:** 001
**Date:** 2026-06-25
**Branch / worktree:** `feat/ask` (`worktrees/ask`)
**Status:** Approved for planning

## Context

This is **Spec A** of a two-part feature line:

- **Spec A (this doc) — Semantic search foundation.** Add vector/embedding search so the
  existing search box finds notes *by meaning*, not just literal tokens, and matches *across
  languages*. Ships as a strictly better search experience and becomes the retrieval substrate
  for Spec B.
- **Spec B (separate doc, later) — the "Ask my notes" agentic assistant.** A natural-language
  plan→retrieve→synthesize loop (e.g. *"Tomorrow I have a 1:1 with Hans, pull together everything
  relevant as a summary"*) built on top of Spec A, with full per-step pipeline logging.

Spec A is designed to deliver **standalone user value in its own PR** — better search for everyone —
independent of Spec B.

### Why this is needed

Today's search (`Search/SearchService.cs`, `Data/MathomDbContext.cs`) is PostgreSQL full-text over a
generated `tsvector` column using the **`simple`** config — language-agnostic but with **no
stemming**. Consequences:

- "meeting" does not match "meetings"; "1:1" does not match "one-on-one".
- A search for "performance review" won't surface a note that says "talked about how Hans is doing".
- Cross-language recall relies entirely on stored translation variants.

Vector embeddings fix all of these: notes are matched by semantic meaning, and modern multilingual
embedding models give cross-language recall for free.

## Decisions (locked during brainstorming)

| Decision | Choice |
|----------|--------|
| Scope | Spec A only now; Spec B is a separate later cycle |
| Embedding provider | **Infomaniak primary → OpenRouter fallback** (mirrors `FallbackLlmClient`) |
| What to embed | **Source note only** (`Title` + `CleanText`), **one vector per note**, multilingual model. No chunking (notes are short). No per-translation vectors. |
| Search rollout | **Hybrid ranking by default** for all users — no toggle |
| Backfill | **Automatic background backfill** on startup, idempotent, batched |
| Fusion method | **Reciprocal Rank Fusion (RRF)** of lexical + semantic results |
| Vector index | **pgvector HNSW**, cosine distance (`vector_cosine_ops`) |

## Goals

1. Every user's search box returns semantically relevant results, including across languages,
   without regressing exact-match behavior.
2. New notes are embedded automatically as part of the existing async pipeline.
3. Existing notes are backfilled automatically with no manual step.
4. Retrieval is observable: logs explain *why* a result ranked where it did.

## Non-goals (deferred to Spec B)

- The agentic plan→retrieve→synthesize loop, entity/person resolution, multi-step query planning.
- Answer synthesis with citations.
- The deep per-step pipeline logging of the Ask flow.
- Re-ranking models, chunking long documents, per-translation-variant vectors.

## Architecture

### Data model

A new **additive** migration (production DB is a persistent volume — never regenerate existing
migrations):

1. `CREATE EXTENSION IF NOT EXISTS vector;`
2. On `Item` (`Domain/Item.cs`):
   - `Embedding` — nullable `vector(N)` column. `N` is **config-driven** and fixed once chosen
     (pgvector requires a fixed dimension per column). Mapped via the Npgsql pgvector plugin
     (`Pgvector.EntityFrameworkCore`).
   - `EmbeddingModel` — nullable `string`, records which model produced the current vector.
   - `EmbeddedAt` — nullable `DateTimeOffset`.
   - The `EmbeddingModel` marker lets the backfill detect notes embedded by an older model and
     re-embed them if the configured model/dimension ever changes.
3. HNSW index on `Item.Embedding` using `vector_cosine_ops`.

Rationale for embedding on `Item` directly (vs. a separate `ItemEmbedding` entity): one vector per
note, 1:1 with the row, source-language only — a separate table adds joins and ceremony for no
benefit at this scope.

### Embedding client (mirrors existing LLM infrastructure)

```
IEmbeddingClient
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
```

- `InfomaniakEmbeddingClient` and `OpenRouterEmbeddingClient` call the providers'
  OpenAI-compatible `/embeddings` endpoint, following the shape of `OpenAiCompatibleLlmClient`.
- `FallbackEmbeddingClient` wraps them (Infomaniak primary → OpenRouter fallback) with the same
  retry/backoff pattern as `FallbackLlmClient`.
- DI wiring in `Program.cs` parallels the existing LLM registration. Config sections
  `Embeddings:Infomaniak` and `Embeddings:OpenRouter` (`Model`, `ApiKey`, `BaseUrl`), plus
  top-level `Embeddings:Model` / `Embeddings:Dimensions` describing the active vector space.

**Open item resolved first in implementation:** confirm Infomaniak's embeddings endpoint, model
name, and dimension count. The chosen model fixes `N` for the `vector(N)` column. The OpenRouter
fallback model must produce vectors of the **same dimension** (or fallback is disabled with a clear
log). This is the first implementation task because the migration depends on `N`.

### Pipeline integration

In `ItemProcessor.ProcessAsync` (`Processing/ItemProcessor.cs`), after LLM cleanup yields
`Title`/`CleanText` and before/alongside the existing translation step:

- Embed `Title + "\n" + CleanText` (source language) via `IEmbeddingClient`; store
  `Embedding`, `EmbeddingModel`, `EmbeddedAt` on the item.
- **Best-effort**, identical to the translation step: a failure logs a warning and leaves
  `Embedding` null; the note still reaches `Status = Ready`. The backfill will fill it in later.
- Re-processing a note re-embeds it (cleanup may have changed the text).

### Backfill

An idempotent background pass that ensures all `Ready` notes have a current vector:

- Selects `Ready` notes where `Embedding IS NULL` **or** `EmbeddingModel <> :currentModel`.
- Processes in batches with a bounded concurrency, honoring the host's cancellation token.
- Runs on startup (a hosted service / one-shot background task), disabled in the `Testing`
  environment like the existing worker. Safe to run repeatedly — re-runs are no-ops once vectors
  are current.
- Uses `.IgnoreQueryFilters()` only as the existing pipeline does for cross-cutting work, while
  still scoping writes per item/owner.

### Hybrid query (core change)

`SearchService.QueryAsync` becomes hybrid when there is a text query:

1. **Lexical signal** — existing `tsvector` / `WebSearchToTsQuery("simple", query)` over `Item` and
   `ItemTranslation`, ranked as today. Strong for exact terms, names, acronyms.
2. **Semantic signal** — embed the query once via `IEmbeddingClient`, then rank the user's notes by
   cosine distance: `ORDER BY embedding <=> :queryVec` (pgvector), top-K.
3. **Fusion** — combine the two ranked lists with **Reciprocal Rank Fusion**:
   `score(d) = Σ 1 / (k + rank_i(d))` over the lists it appears in (constant `k`, e.g. 60). RRF
   needs no score normalization and is robust to scale differences between the two signals.

Behavior preserved:

- Empty / very short queries and filter-only browsing keep today's timeline behavior (no embedding
  call).
- All existing filters (`ItemType`, `Actionable`, tag) and **per-user isolation / soft-delete
  global query filters** apply unchanged — the semantic candidate set is always already scoped to
  the user.
- Notes without an embedding (mid-backfill, or embedding failed) still appear via the lexical
  signal — semantic is strictly additive.

### Observability (foundation slice of the logging requirement)

Structured logs at the retrieval layer for analysis and tuning:

- The raw query text and the embedding model/version used.
- Top-K lexical hits with their `tsvector` ranks.
- Top-K semantic hits with their cosine distances.
- The fused (RRF) ordering actually returned.

Logged at `Debug`/`Information` as appropriate, per request, scoped to the user. This is the
foundation slice; the full per-step agentic-pipeline logging belongs to Spec B.

## Testing strategy

Integration tests already spin up a real `postgres:17` via Testcontainers
(`tests/Mathom.Tests/PostgresFixture.cs`). Changes:

- Use a Postgres image with **pgvector available** (e.g. `pgvector/pgvector:pg17`) so the extension
  and HNSW index load in tests. No in-memory shortcut (consistent with existing constraints).
- `FakeEmbeddingClient` returns **deterministic** vectors (e.g. seeded by text hash) so hybrid
  ranking is unit-testable offline, mirroring `FakeLlmClient` / `FakeTranscriber`.

Coverage:

1. Migration applies and the `vector` extension + HNSW index are created.
2. Processing a note stores `Embedding` / `EmbeddingModel` / `EmbeddedAt`.
3. Embedding failure is best-effort: note still reaches `Ready` with null embedding.
4. Backfill fills null/stale embeddings idempotently and is a no-op on a second run.
5. Hybrid search returns a semantically-relevant but non-lexical match that lexical-only misses,
   while still returning exact-token matches.
6. Per-user isolation, soft-delete, and the type/actionable/tag filters still hold under hybrid.

## Rollout / operational notes

- Migration is additive; schema applies on startup via `Database.Migrate()`.
- Backfill cost is one embedding call per existing note; it runs in the background and is bounded.
  Logged so the operator can see progress and total calls.
- If `Embeddings:Dimensions` ever changes, a new additive migration is required for a new
  `vector(N)` column (pgvector dimension is fixed per column); the `EmbeddingModel` marker drives
  re-embedding. Out of scope to automate now — noted for future awareness.

## Open items (resolved during implementation)

1. **Infomaniak embeddings model + dimension** — confirm endpoint/model and fix `N`. First task.
2. OpenRouter fallback embedding model matching that dimension (or fallback disabled with a log).
3. Final RRF constant `k` and top-K values (start `k = 60`, `K = 50`; tune via the retrieval logs).
