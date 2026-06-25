using System;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class DbContextTests(PostgresFixture fx)
{
    private const string Uid = "dbcontext-tests-user";

    [Fact]
    public async Task CanPersistAndReadItem()
    {
        await fx.EnsureUserAsync(Uid, "dbcontext@example.com");
        await using var db = fx.NewDbContext();
        var item = Item.CreatePending(SourceType.Text, "hello world", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        db.Items.Add(item);
        await db.SaveChangesAsync();

        await using var db2 = fx.NewDbContext();
        var loaded = await db2.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal("hello world", loaded.RawText);
        Assert.Equal(ItemStatus.Pending, loaded.Status);
    }
}
