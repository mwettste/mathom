using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Search;
using Microsoft.Extensions.Logging.Abstractions;
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
        var search = new SearchService(fixture.NewDbContext(), embed,
            NullLogger<SearchService>.Instance);

        var results = await search.QueryAsync(user, null, "appraisal", new SearchFilters(), 10, CancellationToken.None);

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
        var search = new SearchService(fixture.NewDbContext(), embed,
            NullLogger<SearchService>.Instance);

        var results = await search.QueryAsync(user, null, "budget", new SearchFilters(), 10, CancellationToken.None);

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
}
