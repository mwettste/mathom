# Vector / Hybrid Search Foundation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add pgvector-backed semantic search and fuse it with the existing full-text search so the search box finds notes by meaning and across languages, with automatic embedding in the pipeline and a backfill for existing notes.

**Architecture:** A new `IEmbeddingClient` (Infomaniak primary → OpenRouter fallback, mirroring `FallbackLlmClient`) produces one multilingual vector per note from `Title + CleanText`. The vector is stored in a `vector(N)` column on `Item` (pgvector, HNSW + cosine). `ItemProcessor` embeds notes as a best-effort pipeline step; a background worker backfills existing notes. `SearchService.QueryAsync` becomes hybrid: it runs the lexical (`tsvector`) and semantic (cosine) queries and fuses their rankings with Reciprocal Rank Fusion (RRF), falling back to lexical-only if embedding fails.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 10), EF Core 10 + Npgsql 10, PostgreSQL 17, pgvector via `Pgvector` + `Pgvector.EntityFrameworkCore`, xUnit + Testcontainers.

## Global Constraints

- **Migrations are additive only.** Never regenerate existing migrations; add one new additive migration. Schema is applied on startup via `Database.Migrate()` — not manual `dotnet ef database update`.
- **File-scoped namespaces**, `async` + `CancellationToken` throughout, **primary constructors** as the default for DI.
- **Embedding dimension is fixed in exactly one place:** `EmbeddingConfig.Dimensions`. The DB column, the migration, and tests all reference it. pgvector columns have a fixed dimension.
- **Embedding is best-effort:** a failed embedding logs a warning and leaves the note `Ready` with a null vector (identical to how translation failures are handled in `ItemProcessor`).
- **Hybrid is the default** for all users (no toggle). If the query embedding call fails, search falls back to lexical-only ranking.
- **Preserve per-user isolation and soft-delete:** every query stays scoped by `UserId`; the global query filter (`DeletedAt == null`) is never bypassed in search.
- Integration tests use a **pgvector-capable Postgres image** (`pgvector/pgvector:pg17`). There is no in-memory DB shortcut.
- Config uses `__` as the nested-key separator in env/`.env` (e.g. `Embeddings__Infomaniak__ApiKey`).

---

## File Structure

New files:
- `src/Mathom.Web/Embeddings/EmbeddingConfig.cs` — the single source of truth for the vector dimension.
- `src/Mathom.Web/Embeddings/IEmbeddingClient.cs` — interface + result contract.
- `src/Mathom.Web/Embeddings/OpenAiCompatibleEmbeddingClient.cs` — shared `/embeddings` HTTP logic.
- `src/Mathom.Web/Embeddings/InfomaniakEmbeddingClient.cs` — Infomaniak provider.
- `src/Mathom.Web/Embeddings/OpenRouterEmbeddingClient.cs` — OpenRouter provider.
- `src/Mathom.Web/Embeddings/FallbackEmbeddingClient.cs` — ordered fallback wrapper.
- `src/Mathom.Web/Processing/EmbeddingBackfillWorker.cs` — startup backfill hosted service.
- `tests/Mathom.Tests/FakeEmbeddingClient.cs` — deterministic test double.
- `tests/Mathom.Tests/EmbeddingClientTests.cs`, `HybridSearchTests.cs`, `EmbeddingBackfillTests.cs`, `ItemProcessorEmbeddingTests.cs` — tests.
- `src/Mathom.Web/Data/Migrations/<stamp>_AddItemEmbedding.cs` (+ Designer + snapshot update) — scaffolded migration.

Modified files:
- `src/Mathom.Web/Mathom.Web.csproj` — add pgvector packages.
- `src/Mathom.Web/Domain/Item.cs` — add `Embedding`, `EmbeddingModel`, `EmbeddedAt`.
- `src/Mathom.Web/Data/MathomDbContext.cs` — map the `Embedding` column.
- `src/Mathom.Web/Program.cs` — pgvector data source, DI, extension bootstrap, backfill worker.
- `src/Mathom.Web/Processing/ItemProcessor.cs` — embed after cleanup.
- `src/Mathom.Web/Search/SearchService.cs` — hybrid query + RRF + logging.
- `tests/Mathom.Tests/PostgresFixture.cs` — pgvector image + vector-mapped context.
- `src/Mathom.Web/appsettings.json` — `Embeddings` config section.

---

## Task 1: Embedding client abstraction + providers + fallback

**Files:**
- Create: `src/Mathom.Web/Embeddings/EmbeddingConfig.cs`
- Create: `src/Mathom.Web/Embeddings/IEmbeddingClient.cs`
- Create: `src/Mathom.Web/Embeddings/OpenAiCompatibleEmbeddingClient.cs`
- Create: `src/Mathom.Web/Embeddings/InfomaniakEmbeddingClient.cs`
- Create: `src/Mathom.Web/Embeddings/OpenRouterEmbeddingClient.cs`
- Create: `src/Mathom.Web/Embeddings/FallbackEmbeddingClient.cs`
- Create: `tests/Mathom.Tests/FakeEmbeddingClient.cs`
- Test: `tests/Mathom.Tests/EmbeddingClientTests.cs`

**Interfaces:**
- Produces:
  - `EmbeddingConfig.Dimensions` (`const int`)
  - `IEmbeddingClient { string ModelId { get; } Task<float[]> EmbedAsync(string text, CancellationToken ct); }`
  - `FallbackEmbeddingClient(IEnumerable<IEmbeddingClient> providers, ILogger<FallbackEmbeddingClient> logger, TimeSpan? retryDelay = null)`
  - `InfomaniakEmbeddingClient(HttpClient http, IConfiguration config)`, `OpenRouterEmbeddingClient(HttpClient http, IConfiguration config)`
  - `FakeEmbeddingClient : IEmbeddingClient` (test double)

**Provider verification (do this first):** Confirm Infomaniak exposes an OpenAI-compatible `POST {BaseUrl}embeddings` endpoint, the multilingual model name, and its output dimension. Use the real key from `.env` (`Embeddings__Infomaniak__*`) if available:

```bash
curl -s https://api.infomaniak.com/2/ai/<product-id>/openai/v1/embeddings \
  -H "Authorization: Bearer $INFOMANIAK_KEY" -H "Content-Type: application/json" \
  -d '{"model":"<model>","input":"hallo welt"}' | python3 -c "import sys,json;d=json.load(sys.stdin);print(len(d['data'][0]['embedding']))"
```

Record the dimension; it sets `EmbeddingConfig.Dimensions` below. **If the confirmed dimension is not 1024, change the constant in this task before the Task 3 migration is generated.** Confirm the OpenRouter fallback model produces the **same** dimension (otherwise fallback must be disabled — note it and leave OpenRouter unconfigured).

- [ ] **Step 1: Write the dimension constant**

Create `src/Mathom.Web/Embeddings/EmbeddingConfig.cs`:

```csharp
namespace Mathom.Web.Embeddings;

/// <summary>
/// Single source of truth for the embedding vector dimension. The pgvector column,
/// its migration, and tests all reference this — a pgvector column has a fixed dimension,
/// so changing models with a different size requires a new additive migration.
/// </summary>
public static class EmbeddingConfig
{
    // Confirmed from the Infomaniak embeddings model in Task 1's verification step.
    public const int Dimensions = 1024;
}
```

- [ ] **Step 2: Write the interface**

Create `src/Mathom.Web/Embeddings/IEmbeddingClient.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Embeddings;

public interface IEmbeddingClient
{
    /// <summary>Identifier of the active model, stored on the note so the backfill can detect staleness.</summary>
    string ModelId { get; }

    /// <summary>Returns the embedding vector for <paramref name="text"/>. Length == EmbeddingConfig.Dimensions.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
```

- [ ] **Step 3: Write the failing test for the base client + fallback**

Create `tests/Mathom.Tests/EmbeddingClientTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mathom.Tests;

public class EmbeddingClientTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Fact]
    public async Task Parses_embedding_from_openai_shape()
    {
        var http = new HttpClient(new StubHandler("{\"data\":[{\"embedding\":[0.1,0.2,0.3]}]}"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embeddings:OpenRouter:Model"] = "test-model",
            ["Embeddings:OpenRouter:BaseUrl"] = "https://example.test/v1/",
        }).Build();
        var client = new OpenRouterEmbeddingClient(http, config);

        var vec = await client.EmbedAsync("hello", CancellationToken.None);

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, vec);
        Assert.Equal("test-model", client.ModelId);
    }

    private sealed class ThrowingClient : IEmbeddingClient
    {
        public string ModelId => "throws";
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => throw new InvalidOperationException("down");
    }

    private sealed class OkClient : IEmbeddingClient
    {
        public string ModelId => "ok";
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(new[] { 1f, 2f });
    }

    [Fact]
    public async Task Fallback_uses_second_provider_when_first_fails()
    {
        var client = new FallbackEmbeddingClient(
            new IEmbeddingClient[] { new ThrowingClient(), new OkClient() },
            NullLogger<FallbackEmbeddingClient>.Instance,
            retryDelay: TimeSpan.Zero);

        var vec = await client.EmbedAsync("x", CancellationToken.None);

        Assert.Equal(new[] { 1f, 2f }, vec);
        Assert.Equal("throws", client.ModelId); // ModelId reports the primary provider
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EmbeddingClientTests"`
Expected: FAIL — `OpenRouterEmbeddingClient` / `FallbackEmbeddingClient` do not exist (compile error).

- [ ] **Step 5: Write the OpenAI-compatible base client**

Create `src/Mathom.Web/Embeddings/OpenAiCompatibleEmbeddingClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Embeddings;

/// <summary>
/// Shared OpenAI-compatible <c>/embeddings</c> logic. Mirrors OpenAiCompatibleLlmClient:
/// subclasses supply the config section and default base URL; this class owns the HTTP cycle.
/// </summary>
public abstract class OpenAiCompatibleEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _providerName;

    protected OpenAiCompatibleEmbeddingClient(HttpClient http, IConfiguration config, string configSection, string defaultBaseUrl)
    {
        _http = http;
        _providerName = configSection;
        var section = config.GetSection(configSection);
        _model = section["Model"] ?? string.Empty;
        _http.BaseAddress ??= new Uri(section["BaseUrl"] ?? defaultBaseUrl);
        var key = section["ApiKey"];
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", key);
    }

    public string ModelId => _model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException(
                $"Embedding model is not configured for '{_providerName}'. Set {_providerName}:Model and {_providerName}:ApiKey.");

        var payload = new { model = _model, input = text };
        using var resp = await _http.PostAsJsonAsync("embeddings", payload, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vec = new float[arr.GetArrayLength()];
        var i = 0;
        foreach (var el in arr.EnumerateArray())
            vec[i++] = el.GetSingle();
        return vec;
    }
}
```

- [ ] **Step 6: Write the two providers**

Create `src/Mathom.Web/Embeddings/InfomaniakEmbeddingClient.cs`:

```csharp
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Embeddings;

// Infomaniak's OpenAI-compatible embeddings endpoint. The real BaseUrl (with product id,
// e.g. https://api.infomaniak.com/2/ai/<product>/openai/v1/) is supplied via configuration/.env.
public class InfomaniakEmbeddingClient(HttpClient http, IConfiguration config)
    : OpenAiCompatibleEmbeddingClient(http, config, "Embeddings:Infomaniak", "https://api.infomaniak.com/2/ai/")
{
}
```

Create `src/Mathom.Web/Embeddings/OpenRouterEmbeddingClient.cs`:

```csharp
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Embeddings;

public class OpenRouterEmbeddingClient(HttpClient http, IConfiguration config)
    : OpenAiCompatibleEmbeddingClient(http, config, "Embeddings:OpenRouter", "https://openrouter.ai/api/v1/")
{
}
```

- [ ] **Step 7: Write the fallback wrapper**

Create `src/Mathom.Web/Embeddings/FallbackEmbeddingClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Embeddings;

// Mirrors FallbackLlmClient: try each provider AttemptsPerProvider times, in order.
public class FallbackEmbeddingClient(
    IEnumerable<IEmbeddingClient> providers,
    ILogger<FallbackEmbeddingClient> logger,
    TimeSpan? retryDelay = null) : IEmbeddingClient
{
    private const int AttemptsPerProvider = 2;
    private readonly IReadOnlyList<IEmbeddingClient> _providers = providers.ToList();
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);

    // The primary provider defines the stored model id (the vector space notes are embedded into).
    public string ModelId => _providers[0].ModelId;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        Exception? last = null;
        for (var p = 0; p < _providers.Count; p++)
        {
            for (var attempt = 1; attempt <= AttemptsPerProvider; attempt++)
            {
                try
                {
                    return await _providers[p].EmbedAsync(text, ct);
                }
                catch (Exception ex)
                {
                    last = ex;
                    logger.LogWarning(ex, "Embedding provider {Provider} attempt {Attempt} failed",
                        _providers[p].GetType().Name, attempt);
                    var isLastAttempt = attempt == AttemptsPerProvider;
                    var isLastProvider = p == _providers.Count - 1;
                    if (!isLastAttempt || !isLastProvider)
                        await Task.Delay(_retryDelay * attempt, ct);
                }
            }
        }
        throw new InvalidOperationException("All embedding providers failed.", last);
    }
}
```

- [ ] **Step 8: Write the deterministic test double**

Create `tests/Mathom.Tests/FakeEmbeddingClient.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Embeddings;

namespace Mathom.Tests;

// Deterministic embeddings for tests. By default derives a stable vector from the text so
// equal text → equal vector. Tests that need controlled geometry set Embed explicitly.
public class FakeEmbeddingClient : IEmbeddingClient
{
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public string ModelId { get; set; } = "fake-embed-v1";

    public Func<string, float[]> Embed { get; set; } = text =>
    {
        var v = new float[EmbeddingConfig.Dimensions];
        var seed = (uint)text.GetHashCode();
        for (var i = 0; i < v.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            v[i] = (seed % 1000) / 1000f;
        }
        return v;
    };

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("fake embed failure");
        return Task.FromResult(Embed(text));
    }
}
```

- [ ] **Step 9: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~EmbeddingClientTests"`
Expected: PASS (2 tests).

- [ ] **Step 10: Commit**

```bash
git add src/Mathom.Web/Embeddings tests/Mathom.Tests/FakeEmbeddingClient.cs tests/Mathom.Tests/EmbeddingClientTests.cs
git commit -m "feat: embedding client abstraction (Infomaniak + OpenRouter fallback)"
```

---

## Task 2: pgvector packages + data source wiring + DI

**Files:**
- Modify: `src/Mathom.Web/Mathom.Web.csproj`
- Modify: `src/Mathom.Web/Program.cs:14-16` (AddDbContext), `:104-116` (LLM DI block), `:118-119` (hosted services)
- Modify: `src/Mathom.Web/appsettings.json`

**Interfaces:**
- Consumes: `IEmbeddingClient`, `InfomaniakEmbeddingClient`, `OpenRouterEmbeddingClient`, `FallbackEmbeddingClient` (Task 1).
- Produces: `IEmbeddingClient` registered in DI; `MathomDbContext` configured with the pgvector type mapping; `vector` extension ensured on startup.

- [ ] **Step 1: Add pgvector packages**

Edit `src/Mathom.Web/Mathom.Web.csproj`, add to the existing `<ItemGroup>`:

```xml
    <PackageReference Include="Pgvector" Version="0.3.1" />
    <PackageReference Include="Pgvector.EntityFrameworkCore" Version="0.2.2" />
```

- [ ] **Step 2: Wire the vector-enabled data source**

In `src/Mathom.Web/Program.cs`, replace the `AddDbContext` registration (currently lines 14-16):

```csharp
builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mathom")));
```

with:

```csharp
var mathomConnectionString = builder.Configuration.GetConnectionString("Mathom")!;
var mathomDataSource = BuildMathomDataSource(mathomConnectionString);
builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(mathomDataSource, npgsql => npgsql.UseVector()));
```

Add this local function near the bottom of `Program.cs`, just above the trailing `public partial class Program { }` if present (otherwise at end of file):

```csharp
// Builds an Npgsql data source with the pgvector type mapping enabled. Building the
// data source does NOT open a connection, so this is safe at EF design time. The `vector`
// extension is created at startup (below) before the first real query runs.
static Npgsql.NpgsqlDataSource BuildMathomDataSource(string connectionString)
{
    var dsb = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
    dsb.UseVector();
    return dsb.Build();
}
```

- [ ] **Step 3: Ensure the extension exists before migrating**

In `src/Mathom.Web/Program.cs`, inside the existing `if (!app.Environment.IsEnvironment("Testing"))` startup block, **immediately before** `sp.GetRequiredService<MathomDbContext>().Database.Migrate();`, add:

```csharp
    // pgvector's type mapping resolves the `vector` type when a connection first opens.
    // On a fresh DB the extension may not exist yet, so create it on a plain (unmapped)
    // connection first. Idempotent; the migration also declares it.
    using (var bootstrap = new Npgsql.NpgsqlConnection(mathomConnectionString))
    {
        bootstrap.Open();
        using var cmd = bootstrap.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
        cmd.ExecuteNonQuery();
    }
```

(`mathomConnectionString` is the top-level local from Step 2 and is in scope here.)

- [ ] **Step 4: Register the embedding client (mirror the LLM block)**

In `src/Mathom.Web/Program.cs`, after the `AddScoped<ILlmClient>(...)` registration (around line 116), add:

```csharp
builder.Services.AddHttpClient<InfomaniakEmbeddingClient>();
builder.Services.AddHttpClient<OpenRouterEmbeddingClient>();
builder.Services.AddScoped<IEmbeddingClient>(sp => new FallbackEmbeddingClient(
    new IEmbeddingClient[]
    {
        sp.GetRequiredService<InfomaniakEmbeddingClient>(),
        sp.GetRequiredService<OpenRouterEmbeddingClient>(),
    },
    sp.GetRequiredService<ILogger<FallbackEmbeddingClient>>()));
```

Add the using at the top of `Program.cs` (with the other `using Mathom.Web.*;`):

```csharp
using Mathom.Web.Embeddings;
```

- [ ] **Step 5: Add the config section**

Edit `src/Mathom.Web/appsettings.json`, add a sibling of `"Llm"`:

```json
  "Embeddings": {
    "Infomaniak": { "BaseUrl": "https://api.infomaniak.com/2/ai/", "Model": "", "ApiKey": "" },
    "OpenRouter": { "BaseUrl": "https://openrouter.ai/api/v1/", "Model": "", "ApiKey": "" }
  },
```

- [ ] **Step 6: Verify it builds**

Run: `dotnet build src/Mathom.Web/Mathom.Web.csproj`
Expected: Build succeeded (no errors). This task's behavior is exercised by Task 3's integration test.

- [ ] **Step 7: Commit**

```bash
git add src/Mathom.Web/Mathom.Web.csproj src/Mathom.Web/Program.cs src/Mathom.Web/appsettings.json
git commit -m "feat: wire pgvector data source + embedding client DI"
```

---

## Task 3: Item embedding column + migration + test fixture

**Files:**
- Modify: `src/Mathom.Web/Domain/Item.cs`
- Modify: `src/Mathom.Web/Data/MathomDbContext.cs` (Item config block, around lines 22-50)
- Create: `src/Mathom.Web/Data/Migrations/<stamp>_AddItemEmbedding.cs` (+ Designer + snapshot) via `dotnet ef`
- Modify: `tests/Mathom.Tests/PostgresFixture.cs`
- Test: `tests/Mathom.Tests/HybridSearchTests.cs` (roundtrip test only in this task; hybrid query in Task 5)

**Interfaces:**
- Consumes: `EmbeddingConfig.Dimensions` (Task 1), pgvector data source (Task 2).
- Produces: `Item.Embedding` (`Pgvector.Vector?`), `Item.EmbeddingModel` (`string?`), `Item.EmbeddedAt` (`DateTimeOffset?`); a migration creating the column, the `vector` extension, and the HNSW index; `PostgresFixture` running `pgvector/pgvector:pg17` with a vector-mapped `MathomDbContext`.

- [ ] **Step 1: Add the columns to the entity**

Edit `src/Mathom.Web/Domain/Item.cs`, after the `Translations` property:

```csharp
    // Multilingual semantic-search vector over Title + CleanText (source language).
    // Null until embedded (pipeline is best-effort; the backfill fills gaps).
    public Pgvector.Vector? Embedding { get; set; }

    // Model that produced the current Embedding; lets the backfill detect stale vectors.
    public string? EmbeddingModel { get; set; }
    public DateTimeOffset? EmbeddedAt { get; set; }
```

- [ ] **Step 2: Map the column type in the DbContext**

Edit `src/Mathom.Web/Data/MathomDbContext.cs`, inside `b.Entity<Item>(e => { ... })`, after the generated tsvector block and before the closing `});`:

```csharp
            // Semantic-search embedding. Fixed dimension (EmbeddingConfig.Dimensions); the
            // HNSW cosine index is created in the AddItemEmbedding migration (raw SQL).
            e.Property(x => x.Embedding)
                .HasColumnType($"vector({Mathom.Web.Embeddings.EmbeddingConfig.Dimensions})");
```

- [ ] **Step 3: Update the test fixture to a pgvector image + vector mapping**

Edit `tests/Mathom.Tests/PostgresFixture.cs`. Replace the container field and `NewDbContext`/`InitializeAsync` so the image supports pgvector and the context maps the `vector` type:

```csharp
using System.Threading.Tasks;
using Mathom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Mathom.Tests;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    private NpgsqlDataSource _dataSource = null!;

    public string ConnectionString => _container.GetConnectionString();

    public MathomDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MathomDbContext>()
            .UseNpgsql(_dataSource, o => o.UseVector())
            .Options;
        return new MathomDbContext(options);
    }

    public async Task EnsureUserAsync(string id, string email)
    {
        await using var db = NewDbContext();
        if (await db.Users.FindAsync(id) is not null) return;
        db.Users.Add(new Mathom.Web.Domain.ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = id,
        });
        await db.SaveChangesAsync();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Create the extension before building the vector-mapped data source (its type
        // resolution needs `vector` to exist), then migrate.
        await using (var bootstrap = new NpgsqlConnection(ConnectionString))
        {
            await bootstrap.OpenAsync();
            await using var cmd = bootstrap.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
            await cmd.ExecuteNonQueryAsync();
        }

        var dsb = new NpgsqlDataSourceBuilder(ConnectionString);
        dsb.UseVector();
        _dataSource = dsb.Build();

        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
```

- [ ] **Step 4: Scaffold the migration**

Run:

```bash
dotnet ef migrations add AddItemEmbedding --project src/Mathom.Web --startup-project src/Mathom.Web
```

Expected: creates `src/Mathom.Web/Data/Migrations/<stamp>_AddItemEmbedding.cs`, its `.Designer.cs`, and updates `MathomDbContextModelSnapshot.cs`. EF scaffolds the three `AddColumn` calls (Embedding as `vector(1024)`, EmbeddingModel, EmbeddedAt).

- [ ] **Step 5: Edit the migration to add the extension + HNSW index**

Open the generated `<stamp>_AddItemEmbedding.cs`. Ensure `using Pgvector;` is present. Make `Up` start by creating the extension and end by creating the index; make `Down` drop the index. The body should read:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

    // (EF-scaffolded AddColumn calls for Embedding, EmbeddingModel, EmbeddedAt remain here.)

    migrationBuilder.Sql(
        @"CREATE INDEX ""IX_Items_Embedding"" ON ""Items"" USING hnsw (""Embedding"" vector_cosine_ops);");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Items_Embedding"";");

    // (EF-scaffolded DropColumn calls remain here.)
}
```

- [ ] **Step 6: Write the roundtrip test**

Create `tests/Mathom.Tests/HybridSearchTests.cs` with the migration/roundtrip test (hybrid query tests are added in Task 5):

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Pgvector;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class HybridSearchTests(PostgresFixture fixture)
{
    private static float[] UnitVector(int dim, int hot)
    {
        var v = new float[dim];
        v[hot] = 1f;
        return v;
    }

    [Fact]
    public async Task Embedding_roundtrips_through_pgvector_column()
    {
        await fixture.EnsureUserAsync("u-emb", "u-emb@example.com");
        var id = Guid.NewGuid();
        await using (var db = fixture.NewDbContext())
        {
            db.Items.Add(new Item
            {
                Id = id, UserId = "u-emb", Status = ItemStatus.Ready, SourceType = SourceType.Text,
                RawText = "x", Title = "t", CleanText = "c", IdempotencyKey = id.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
                Embedding = new Vector(UnitVector(Mathom.Web.Embeddings.EmbeddingConfig.Dimensions, 3)),
                EmbeddingModel = "fake-embed-v1", EmbeddedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.NewDbContext())
        {
            var loaded = await db.Items.FindAsync(id);
            Assert.NotNull(loaded!.Embedding);
            Assert.Equal(Mathom.Web.Embeddings.EmbeddingConfig.Dimensions, loaded.Embedding!.ToArray().Length);
            Assert.Equal(1f, loaded.Embedding.ToArray()[3]);
        }
    }
}
```

- [ ] **Step 7: Run the test**

Run: `dotnet test --filter "FullyQualifiedName~HybridSearchTests"`
Expected: PASS — migration applies (extension + column + HNSW index) and the vector roundtrips.

- [ ] **Step 8: Commit**

```bash
git add src/Mathom.Web/Domain/Item.cs src/Mathom.Web/Data tests/Mathom.Tests/PostgresFixture.cs tests/Mathom.Tests/HybridSearchTests.cs
git commit -m "feat: add Item.Embedding pgvector column + migration + test fixture"
```

---

## Task 4: Embed notes in the processing pipeline (best-effort)

**Files:**
- Modify: `src/Mathom.Web/Processing/ItemProcessor.cs` (constructor + after cleanup, around lines 95-107)
- Test: `tests/Mathom.Tests/ItemProcessorEmbeddingTests.cs`

**Interfaces:**
- Consumes: `IEmbeddingClient` (Task 1), `Item.Embedding/EmbeddingModel/EmbeddedAt` (Task 3), `FakeEmbeddingClient` (Task 1).
- Produces: after processing, a `Ready` note has `Embedding` set (or null on failure) plus `EmbeddingModel`/`EmbeddedAt`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Mathom.Tests/ItemProcessorEmbeddingTests.cs`. This follows the existing `ItemProcessorTests` construction pattern (use that file as the reference for building an `ItemProcessor` with fakes + the fixture):

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ItemProcessorEmbeddingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Processing_stores_embedding_and_model()
    {
        await fixture.EnsureUserAsync("u-pe", "u-pe@example.com");
        var embed = new FakeEmbeddingClient { ModelId = "m-1" };
        var id = await ProcessingTestHarness.CaptureAndProcessAsync(fixture, "u-pe", "buy milk", embed);

        await using var db = fixture.NewDbContext();
        var item = await db.Items.FindAsync(id);
        Assert.Equal(ItemStatus.Ready, item!.Status);
        Assert.NotNull(item.Embedding);
        Assert.Equal("m-1", item.EmbeddingModel);
        Assert.NotNull(item.EmbeddedAt);
        Assert.True(embed.Calls >= 1);
    }

    [Fact]
    public async Task Embedding_failure_is_best_effort_note_still_ready()
    {
        await fixture.EnsureUserAsync("u-pe2", "u-pe2@example.com");
        var embed = new FakeEmbeddingClient { Throw = true };
        var id = await ProcessingTestHarness.CaptureAndProcessAsync(fixture, "u-pe2", "call alice", embed);

        await using var db = fixture.NewDbContext();
        var item = await db.Items.FindAsync(id);
        Assert.Equal(ItemStatus.Ready, item!.Status);
        Assert.Null(item.Embedding);
        Assert.Null(item.EmbeddingModel);
    }
}
```

Add a small harness `tests/Mathom.Tests/ProcessingTestHarness.cs` that constructs an `ItemProcessor` the same way the existing `ItemProcessorTests` does, but takes an `IEmbeddingClient`. Mirror the existing test's dependency construction exactly (read `ItemProcessorTests.cs` first and copy its fake wiring for `ILlmClient`, `ITranscriber`, `IImageReader`, `IMediaStore`, `PhotoVariantService`, `GlossaryService`, `UserLanguageService`, loggers). Signature:

```csharp
public static class ProcessingTestHarness
{
    public static Task<Guid> CaptureAndProcessAsync(
        PostgresFixture fixture, string userId, string rawText, Mathom.Web.Embeddings.IEmbeddingClient embeddings);
}
```

It inserts a `Pending` text `Item` (via `Item.CreatePending`), builds an `ItemProcessor` with the given `embeddings`, calls `ProcessAsync`, and returns the item id.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ItemProcessorEmbeddingTests"`
Expected: FAIL — `ItemProcessor` has no `IEmbeddingClient` parameter (compile error in the harness).

- [ ] **Step 3: Inject the embedding client**

Edit `src/Mathom.Web/Processing/ItemProcessor.cs`, add a parameter to the primary constructor (after `userLanguages`):

```csharp
    Mathom.Web.Languages.UserLanguageService userLanguages,
    Mathom.Web.Embeddings.IEmbeddingClient embeddings,
    ILogger<ItemProcessor> logger)
```

- [ ] **Step 4: Embed after cleanup (best-effort)**

In `ItemProcessor.ProcessAsync`, after the block that sets `item.Title`/`item.CleanText`/etc. and `item.Error = null` (just before the "Re-translate from scratch" comment), add:

```csharp
            // Semantic-search embedding (source language). Best-effort: a failure leaves the
            // note Ready with a null vector — the backfill will fill it in later.
            try
            {
                var vector = await embeddings.EmbedAsync($"{result.Title}\n{result.CleanText}", ct);
                item.Embedding = new Pgvector.Vector(vector);
                item.EmbeddingModel = embeddings.ModelId;
                item.EmbeddedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception eex)
            {
                logger.LogWarning(eex, "Embedding failed for item {ItemId}", item.Id);
                item.Embedding = null;
                item.EmbeddingModel = null;
            }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ItemProcessorEmbeddingTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mathom.Web/Processing/ItemProcessor.cs tests/Mathom.Tests/ItemProcessorEmbeddingTests.cs tests/Mathom.Tests/ProcessingTestHarness.cs
git commit -m "feat: embed notes during processing (best-effort)"
```

---

## Task 5: Hybrid query with RRF fusion + retrieval logging

**Files:**
- Modify: `src/Mathom.Web/Search/SearchService.cs` (constructor + `QueryAsync`)
- Test: `tests/Mathom.Tests/HybridSearchTests.cs` (add hybrid cases)

**Interfaces:**
- Consumes: `IEmbeddingClient` (Task 1), `Item.Embedding` (Task 3), `Pgvector.EntityFrameworkCore` `CosineDistance`.
- Produces: `SearchService.QueryAsync` returns results fused from lexical + semantic rankings; signature unchanged.

- [ ] **Step 1: Write the failing hybrid tests**

Add to `tests/Mathom.Tests/HybridSearchTests.cs`:

```csharp
    [Fact]
    public async Task Semantic_match_surfaces_when_lexical_misses()
    {
        const string user = "u-hyb";
        await fixture.EnsureUserAsync(user, "u-hyb@example.com");
        var dim = Mathom.Web.Embeddings.EmbeddingConfig.Dimensions;

        // Target note shares NO query tokens but is the nearest vector.
        var target = await SeedReadyAsync(user, "Performance discussion", "How the team is doing", UnitVector(dim, 7));
        await SeedReadyAsync(user, "Grocery list", "milk and eggs", UnitVector(dim, 1));

        // Query has no lexical overlap with the target; its vector is closest to the target's.
        var embed = new FakeEmbeddingClient { Embed = _ => UnitVector(dim, 7) };
        var search = new Mathom.Web.Search.SearchService(fixture.NewDbContext(), embed,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Search.SearchService>.Instance);

        var results = await search.QueryAsync(user, "appraisal", new Mathom.Web.Search.SearchFilters(), 10, CancellationToken.None);

        Assert.Contains(results, r => r.Id == target);
    }

    [Fact]
    public async Task Lexical_only_when_query_embedding_fails()
    {
        const string user = "u-hyb2";
        await fixture.EnsureUserAsync(user, "u-hyb2@example.com");
        var dim = Mathom.Web.Embeddings.EmbeddingConfig.Dimensions;
        var lex = await SeedReadyAsync(user, "Quarterly budget", "numbers and budget figures", UnitVector(dim, 2));

        var embed = new FakeEmbeddingClient { Throw = true }; // embedding down → lexical fallback
        var search = new Mathom.Web.Search.SearchService(fixture.NewDbContext(), embed,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Search.SearchService>.Instance);

        var results = await search.QueryAsync(user, "budget", new Mathom.Web.Search.SearchFilters(), 10, CancellationToken.None);

        Assert.Contains(results, r => r.Id == lex);
    }

    private async Task<Guid> SeedReadyAsync(string user, string title, string clean, float[] embedding)
    {
        var id = Guid.NewGuid();
        await using var db = fixture.NewDbContext();
        db.Items.Add(new Item
        {
            Id = id, UserId = user, Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = clean, Title = title, CleanText = clean, IdempotencyKey = id.ToString(),
            CreatedAt = DateTimeOffset.UtcNow, Embedding = new Vector(embedding),
            EmbeddingModel = "fake-embed-v1", EmbeddedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~HybridSearchTests"`
Expected: FAIL — `SearchService` has no constructor taking `IEmbeddingClient` + logger (compile error).

- [ ] **Step 3: Update the SearchService constructor**

Edit `src/Mathom.Web/Search/SearchService.cs`. Change the class declaration and add usings:

```csharp
using Mathom.Web.Embeddings;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
```

```csharp
public class SearchService(
    MathomDbContext db,
    IEmbeddingClient embeddings,
    ILogger<SearchService> logger)
{
    private const int CandidateK = 50;
```

- [ ] **Step 4: Replace the hybrid branch of `QueryAsync`**

Replace the body of `QueryAsync` from `var hasQuery = ...` through the final `return await items...ToListAsync(ct);` with the hybrid implementation. The no-query (timeline) and filter behavior is unchanged; only the text-query path becomes hybrid:

```csharp
        var baseItems = db.Items.Where(i => i.UserId == userId);

        if (filters.ItemType is { } ft) baseItems = baseItems.Where(i => i.ItemType == ft);
        if (filters.Actionable is { } fa) baseItems = baseItems.Where(i => i.Actionable == fa);
        if (!string.IsNullOrWhiteSpace(filters.Tag))
        {
            var tag = filters.Tag!.ToLower();
            baseItems = baseItems.Where(i => i.ItemTags.Any(it => it.Tag.Name.ToLower() == tag));
        }

        var hasQuery = !string.IsNullOrWhiteSpace(q);
        if (!hasQuery)
        {
            return await Project(baseItems.OrderByDescending(i => i.CreatedAt).Take(take), ct);
        }

        var query = q!;
        var tsq = EF.Functions.WebSearchToTsQuery("simple", query);
        var ready = baseItems.Where(i => i.Status == ItemStatus.Ready);

        // Lexical candidates (source + translation variants), ranked by tsvector rank.
        var lexical = await ready
            .Where(i => i.SearchVector!.Matches(tsq) || i.Translations.Any(t => t.SearchVector!.Matches(tsq)))
            .OrderByDescending(i => i.SearchVector!.Rank(tsq))
            .Take(CandidateK)
            .Select(i => i.Id)
            .ToListAsync(ct);

        // Semantic candidates, ranked by cosine distance — only if we can embed the query.
        var semantic = new List<Guid>();
        Vector? queryVector = null;
        try
        {
            queryVector = new Vector(await embeddings.EmbedAsync(query, ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query embedding failed; falling back to lexical-only search.");
        }

        if (queryVector is not null)
        {
            var ranked = await ready
                .Where(i => i.Embedding != null)
                .Select(i => new { i.Id, Distance = i.Embedding!.CosineDistance(queryVector) })
                .OrderBy(x => x.Distance)
                .Take(CandidateK)
                .ToListAsync(ct);
            semantic = ranked.Select(x => x.Id).ToList();
        }

        var fused = ReciprocalRankFusion(lexical, semantic);
        var topIds = fused.Take(take).ToList();

        logger.LogDebug(
            "Hybrid search user={User} q={Query} model={Model} lexical={LexCount} semantic={SemCount} returned={Returned}",
            userId, query, embeddings.ModelId, lexical.Count, semantic.Count, topIds.Count);

        if (topIds.Count == 0) return new List<ItemSummary>();

        var summaries = await Project(baseItems.Where(i => topIds.Contains(i.Id)), ct);
        var order = topIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        return summaries.OrderBy(s => order[s.Id]).ToList();
    }

    // Reciprocal Rank Fusion: score(d) = Σ 1 / (k + rank). Robust to the two signals' different
    // score scales; an item in both lists is boosted.
    private static List<Guid> ReciprocalRankFusion(IReadOnlyList<Guid> a, IReadOnlyList<Guid> b, int k = 60)
    {
        var scores = new Dictionary<Guid, double>();
        static void Accumulate(Dictionary<Guid, double> s, IReadOnlyList<Guid> list, int k)
        {
            for (var rank = 0; rank < list.Count; rank++)
                s[list[rank]] = s.GetValueOrDefault(list[rank]) + 1.0 / (k + rank + 1);
        }
        Accumulate(scores, a, k);
        Accumulate(scores, b, k);
        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    private static async Task<List<ItemSummary>> Project(IQueryable<Item> items, CancellationToken ct)
    {
        return await items
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList(),
                i.SourceLanguage,
                i.Translations.Select(t => new TranslationSummary(t.Locale, t.Title, t.CleanText)).ToList()))
            .ToListAsync(ct);
    }
```

(Remove the now-unused old `items`/`hasQuery` ordering code that this replaces. `TimelineAsync` and `GetAsync` are untouched.)

- [ ] **Step 5: Run the hybrid + roundtrip tests**

Run: `dotnet test --filter "FullyQualifiedName~HybridSearchTests"`
Expected: PASS (roundtrip + 2 hybrid tests).

- [ ] **Step 6: Run the existing search tests for regressions**

Run: `dotnet test --filter "FullyQualifiedName~SearchServiceTests"`
Expected: PASS — existing lexical behavior (and filters/isolation) still holds. If `SearchServiceTests` constructs `SearchService` directly, update those constructions to pass a `FakeEmbeddingClient` and `NullLogger<SearchService>.Instance`.

- [ ] **Step 7: Commit**

```bash
git add src/Mathom.Web/Search/SearchService.cs tests/Mathom.Tests/HybridSearchTests.cs tests/Mathom.Tests/SearchServiceTests.cs
git commit -m "feat: hybrid lexical+semantic search with RRF fusion"
```

---

## Task 6: Automatic background backfill for existing notes

**Files:**
- Create: `src/Mathom.Web/Processing/EmbeddingBackfillWorker.cs`
- Modify: `src/Mathom.Web/Program.cs` (register hosted service next to `ProcessingWorker`)
- Test: `tests/Mathom.Tests/EmbeddingBackfillTests.cs`

**Interfaces:**
- Consumes: `IEmbeddingClient` (Task 1), `Item.Embedding/EmbeddingModel` (Task 3), `MathomDbContext`.
- Produces: `EmbeddingBackfillWorker.BackfillBatchAsync(MathomDbContext db, IEmbeddingClient embeddings, int batchSize, CancellationToken ct)` returning the number of notes embedded; a hosted service that runs it to completion on startup (non-Testing only).

- [ ] **Step 1: Write the failing test**

Create `tests/Mathom.Tests/EmbeddingBackfillTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class EmbeddingBackfillTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Backfills_null_and_stale_then_is_noop()
    {
        const string user = "u-bf";
        await fixture.EnsureUserAsync(user, "u-bf@example.com");
        var embed = new FakeEmbeddingClient { ModelId = "current" };

        await SeedAsync(user, ItemStatus.Ready, embedding: false, model: null);                 // null → embed
        await SeedAsync(user, ItemStatus.Ready, embedding: true, model: "old");                 // stale → re-embed
        await SeedAsync(user, ItemStatus.Ready, embedding: true, model: "current");             // current → skip
        await SeedAsync(user, ItemStatus.Pending, embedding: false, model: null);               // not Ready → skip

        int first;
        await using (var db = fixture.NewDbContext())
            first = await EmbeddingBackfillWorker.BackfillBatchAsync(db, embed, batchSize: 100, CancellationToken.None);
        Assert.Equal(2, first);

        int second;
        await using (var db = fixture.NewDbContext())
            second = await EmbeddingBackfillWorker.BackfillBatchAsync(db, embed, batchSize: 100, CancellationToken.None);
        Assert.Equal(0, second); // idempotent

        await using (var verify = fixture.NewDbContext())
        {
            var ready = verify.Items.Where(i => i.UserId == user && i.Status == ItemStatus.Ready).ToList();
            Assert.All(ready, i => Assert.Equal("current", i.EmbeddingModel));
        }
    }

    private async Task SeedAsync(string user, ItemStatus status, bool embedding, string? model)
    {
        var id = Guid.NewGuid();
        await using var db = fixture.NewDbContext();
        var dim = Mathom.Web.Embeddings.EmbeddingConfig.Dimensions;
        db.Items.Add(new Item
        {
            Id = id, UserId = user, Status = status, SourceType = SourceType.Text,
            RawText = "r", Title = "t", CleanText = "c", IdempotencyKey = id.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            Embedding = embedding ? new Pgvector.Vector(new float[dim]) : null,
            EmbeddingModel = model,
        });
        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~EmbeddingBackfillTests"`
Expected: FAIL — `EmbeddingBackfillWorker` does not exist (compile error).

- [ ] **Step 3: Write the backfill worker**

Create `src/Mathom.Web/Processing/EmbeddingBackfillWorker.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

// On startup, embeds Ready notes that have no current vector (null or produced by an older
// model). Idempotent and best-effort; runs to completion then idles. Disabled under Testing.
public class EmbeddingBackfillWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<EmbeddingBackfillWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int embedded, total = 0;
            do
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MathomDbContext>();
                var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
                embedded = await BackfillBatchAsync(db, embeddings, BatchSize, stoppingToken);
                total += embedded;
            } while (embedded > 0 && !stoppingToken.IsCancellationRequested);

            if (total > 0) logger.LogInformation("Embedding backfill complete: {Total} note(s) embedded.", total);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Embedding backfill worker failed.");
        }
    }

    // Embeds up to batchSize Ready notes lacking a current-model vector. Returns the count embedded.
    public static async Task<int> BackfillBatchAsync(
        MathomDbContext db, IEmbeddingClient embeddings, int batchSize, CancellationToken ct)
    {
        var model = embeddings.ModelId;
        var batch = await db.Items
            .Where(i => i.Status == ItemStatus.Ready
                     && (i.Embedding == null || i.EmbeddingModel != model))
            .OrderBy(i => i.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        var count = 0;
        foreach (var item in batch)
        {
            try
            {
                var vector = await embeddings.EmbedAsync($"{item.Title}\n{item.CleanText}", ct);
                item.Embedding = new Pgvector.Vector(vector);
                item.EmbeddingModel = model;
                item.EmbeddedAt = DateTimeOffset.UtcNow;
                count++;
            }
            catch (Exception)
            {
                // Best-effort: leave this note for a later run rather than failing the batch.
            }
        }
        await db.SaveChangesAsync(ct);
        return count;
    }
}
```

- [ ] **Step 4: Register the worker**

In `src/Mathom.Web/Program.cs`, where `ProcessingWorker` is registered:

```csharp
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ProcessingWorker>();
    builder.Services.AddHostedService<EmbeddingBackfillWorker>();
}
```

- [ ] **Step 5: Run the test**

Run: `dotnet test --filter "FullyQualifiedName~EmbeddingBackfillTests"`
Expected: PASS — embeds null + stale (2), skips current + non-Ready, second run is a no-op.

- [ ] **Step 6: Commit**

```bash
git add src/Mathom.Web/Processing/EmbeddingBackfillWorker.cs src/Mathom.Web/Program.cs tests/Mathom.Tests/EmbeddingBackfillTests.cs
git commit -m "feat: automatic background embedding backfill"
```

---

## Final verification

- [ ] **Run the full suite**

Run: `just test` (or `dotnet test`)
Expected: all tests pass. Requires Docker (Testcontainers pulls `pgvector/pgvector:pg17`).

- [ ] **Manual smoke (optional)**

With `Embeddings__Infomaniak__*` configured in `.env`, run `just up`, capture a couple of notes, wait for them to reach Ready, and search with a paraphrase that shares no tokens with a note — confirm it surfaces. Check logs for the `Hybrid search ...` debug line.

---

## Self-Review

**Spec coverage:**
- Data model (pgvector column, HNSW, model marker) → Task 3. ✓
- Embedding client (Infomaniak→OpenRouter fallback) → Tasks 1–2. ✓
- Pipeline embedding (best-effort) → Task 4. ✓
- Automatic background backfill → Task 6. ✓
- Hybrid query + RRF + lexical fallback → Task 5. ✓
- Observability (retrieval logging) → Task 5 (`LogDebug` line). ✓
- Testing (pgvector image, FakeEmbeddingClient, isolation/filters preserved) → Tasks 1, 3, 5. ✓
- Source-only / one vector per note → Task 4 embeds `Title+CleanText` only. ✓
- Open item: Infomaniak model + dimension → Task 1 verification step sets `EmbeddingConfig.Dimensions`. ✓

**Placeholder scan:** No TBD/TODO. The one externally-dependent value (vector dimension) is resolved by Task 1's verification step and pinned in `EmbeddingConfig.Dimensions` (default 1024, change-before-Task-3 instruction). The `ProcessingTestHarness` step references the existing `ItemProcessorTests` for exact fake wiring rather than guessing those constructor signatures — read that file when implementing.

**Type consistency:** `IEmbeddingClient.ModelId` / `EmbedAsync(string, ct)` used identically across Tasks 1, 4, 5, 6. `Pgvector.Vector` for the column and all vector construction. `EmbeddingConfig.Dimensions` referenced everywhere a length is needed. `BackfillBatchAsync` signature matches between worker and test. `SearchService` constructor `(MathomDbContext, IEmbeddingClient, ILogger<SearchService>)` matches its test construction.
