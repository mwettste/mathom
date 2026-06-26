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

        // Pre-drain any Ready items left by prior tests so our count assertions are deterministic.
        await using (var drain = fixture.NewDbContext())
            while (await EmbeddingBackfillWorker.BackfillBatchAsync(drain, embed, batchSize: 100, CancellationToken.None) > 0) { }

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
