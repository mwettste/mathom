using System;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class DbContextTests
{
    private readonly PostgresFixture _fx;
    public DbContextTests(PostgresFixture fx) => _fx = fx;

    private const string Uid = "dbcontext-tests-user";

    [Fact]
    public async Task CanPersistAndReadItem()
    {
        await _fx.EnsureUserAsync(Uid, "dbcontext@example.com");
        await using var db = _fx.NewDbContext();
        var item = Item.CreatePending(SourceType.Text, "hello world", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        db.Items.Add(item);
        await db.SaveChangesAsync();

        await using var db2 = _fx.NewDbContext();
        var loaded = await db2.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal("hello world", loaded.RawText);
        Assert.Equal(ItemStatus.Pending, loaded.Status);
    }
}
