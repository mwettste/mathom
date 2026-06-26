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
