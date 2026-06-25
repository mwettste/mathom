using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ProcessingWorkerTests(PostgresFixture fx)
{
    private const string Uid = "worker-tests-user";

    [Fact]
    public async Task ClaimNextPending_ReturnsOldestAndMarksProcessing()
    {
        await fx.EnsureUserAsync(Uid, "worker@example.com");
        var older = Item.CreatePending(SourceType.Text, "older", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = Item.CreatePending(SourceType.Text, "newer", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.AddRange(newer, older);
            await seed.SaveChangesAsync();
        }

        await using var db = fx.NewDbContext();
        var claimed = await ProcessingWorker.ClaimNextPendingIdAsync(db, CancellationToken.None);

        Assert.Equal(older.Id, claimed);
        await using var verify = fx.NewDbContext();
        Assert.Equal(ItemStatus.Processing, (await verify.Items.SingleAsync(i => i.Id == older.Id)).Status);
    }

    [Fact]
    public async Task ClaimNextPending_ReturnsNull_WhenNonePending()
    {
        await using var db = fx.NewDbContext();
        // Mark any leftover pending rows so this test is isolated.
        await db.Items.Where(i => i.Status == ItemStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ItemStatus.Ready));

        var claimed = await ProcessingWorker.ClaimNextPendingIdAsync(db, CancellationToken.None);
        Assert.Null(claimed);
    }

    [Fact]
    public async Task ResetOrphanedProcessing_FlipsProcessingItemBackToPending()
    {
        // Seed an item directly in Processing status (simulating a crash-orphaned item).
        await fx.EnsureUserAsync(Uid, "worker@example.com");
        var orphaned = Item.CreatePending(SourceType.Text, "orphaned", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.Add(orphaned);
            await seed.SaveChangesAsync();
            await seed.Items
                .Where(i => i.Id == orphaned.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ItemStatus.Processing));
        }

        await using var db = fx.NewDbContext();
        var resetCount = await ProcessingWorker.ResetOrphanedProcessingAsync(db, CancellationToken.None);

        Assert.True(resetCount >= 1);

        await using var verify = fx.NewDbContext();
        var item = await verify.Items.SingleAsync(i => i.Id == orphaned.Id);
        Assert.Equal(ItemStatus.Pending, item.Status);
    }

    [Fact]
    public async Task ClaimNext_SkipsSoftDeletedPending()
    {
        var u = "worker-softdelete-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var trashed = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Pending, SourceType = SourceType.Text,
            RawText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow, UserId = u, DeletedAt = DateTimeOffset.UtcNow,
        };
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(trashed); await seed.SaveChangesAsync(); }

        await using var db = fx.NewDbContext();
        var claimed = await ProcessingWorker.ClaimNextPendingIdAsync(db, CancellationToken.None);

        Assert.NotEqual(trashed.Id, claimed); // never claims a trashed pending item
    }
}
