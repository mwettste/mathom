using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ProcessingWorkerTests
{
    private readonly PostgresFixture _fx;
    public ProcessingWorkerTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ClaimNextPending_ReturnsOldestAndMarksProcessing()
    {
        var older = Item.CreatePending(SourceType.Text, "older", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = Item.CreatePending(SourceType.Text, "newer", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext())
        {
            seed.Items.AddRange(newer, older);
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.NewDbContext();
        var claimed = await ProcessingWorker.ClaimNextPendingIdAsync(db, CancellationToken.None);

        Assert.Equal(older.Id, claimed);
        await using var verify = _fx.NewDbContext();
        Assert.Equal(ItemStatus.Processing, (await verify.Items.SingleAsync(i => i.Id == older.Id)).Status);
    }

    [Fact]
    public async Task ClaimNextPending_ReturnsNull_WhenNonePending()
    {
        await using var db = _fx.NewDbContext();
        // Mark any leftover pending rows so this test is isolated.
        await db.Items.Where(i => i.Status == ItemStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ItemStatus.Ready));

        var claimed = await ProcessingWorker.ClaimNextPendingIdAsync(db, CancellationToken.None);
        Assert.Null(claimed);
    }
}
