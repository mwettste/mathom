using System;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ItemCaptureNoteTests(PostgresFixture fx)
{
    private const string Uid = "u-note";

    [Fact]
    public async Task CaptureNote_RoundTrips()
    {
        await fx.EnsureUserAsync(Uid, Uid + "@example.com");
        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        item.CaptureNote = "receipt from the hardware store";

        await using (var db = fx.NewDbContext())
        {
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }

        await using (var db = fx.NewDbContext())
        {
            var loaded = await db.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == item.Id);
            Assert.Equal("receipt from the hardware store", loaded.CaptureNote);
        }
    }
}
