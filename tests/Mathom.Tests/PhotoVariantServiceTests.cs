using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PhotoVariantServiceTests(PostgresFixture fx)
{
    private static async Task<byte[]> MakePngBytesAsync(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        await img.SaveAsync(ms, new PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public async Task EnsureDisplay_GeneratesVariant_SetsDisplayPath_AndIsIdempotent()
    {
        await fx.EnsureUserAsync("user-1", "user-1@example.com");

        var media = new FakeMediaStore();
        string originalKey;
        using (var s = new MemoryStream(await MakePngBytesAsync(3000, 2000)))
            originalKey = await media.SaveAsync(s, ".png", CancellationToken.None);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), "user-1", DateTimeOffset.UtcNow);
        var photo = new ItemPhoto { MediaPath = originalKey, Order = 0, CreatedAt = DateTimeOffset.UtcNow };
        item.Photos.Add(photo);
        await using (var db = fx.NewDbContext()) { db.Items.Add(item); await db.SaveChangesAsync(); }

        await using (var db = fx.NewDbContext())
        {
            var tracked = await db.ItemPhotos.IgnoreQueryFilters().FirstAsync(p => p.Id == photo.Id);
            var svc = new PhotoVariantService(db, media, new ImageVariantProcessor());

            var key1 = await svc.EnsureDisplayAsync(tracked, CancellationToken.None);
            var key2 = await svc.EnsureDisplayAsync(tracked, CancellationToken.None);

            Assert.Equal(key1, key2);                 // idempotent
            Assert.True(media.Has(key1));             // variant stored
            Assert.EndsWith(".jpg", key1);
        }

        await using (var db = fx.NewDbContext())
        {
            var reloaded = await db.ItemPhotos.IgnoreQueryFilters().FirstAsync(p => p.Id == photo.Id);
            Assert.False(string.IsNullOrEmpty(reloaded.DisplayPath));
        }
    }
}
