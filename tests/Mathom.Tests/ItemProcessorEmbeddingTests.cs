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
