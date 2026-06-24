using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ItemPhotoTests
{
    private const string Uid = "itemphoto-user";
    private readonly PostgresFixture _fx;
    public ItemPhotoTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Item_PersistsPhotos_AndCascadeDeletesThem()
    {
        await _fx.EnsureUserAsync(Uid, Uid + "@example.com");
        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = "a.jpg", Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = "b.jpg", Order = 1, CreatedAt = DateTimeOffset.UtcNow });

        await using (var seed = _fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        await using (var read = _fx.NewDbContext())
        {
            var loaded = await read.Items.Include(i => i.Photos).SingleAsync(i => i.Id == item.Id);
            Assert.Equal(2, loaded.Photos.Count);
            Assert.Equal(new[] { "a.jpg", "b.jpg" }, loaded.Photos.OrderBy(p => p.Order).Select(p => p.MediaPath));
        }

        await using (var del = _fx.NewDbContext())
        {
            var loaded = await del.Items.SingleAsync(i => i.Id == item.Id);
            del.Items.Remove(loaded);
            await del.SaveChangesAsync();
        }

        await using (var verify = _fx.NewDbContext())
        {
            Assert.False(await verify.Set<ItemPhoto>().AnyAsync(p => p.ItemId == item.Id));
        }
    }
}
