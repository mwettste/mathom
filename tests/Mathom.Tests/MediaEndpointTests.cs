using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class MediaEndpointTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> CreateAppAsync(FakeMediaStore media)
    {
        var app = new TestWebAppFactory(fx.ConnectionString, s =>
        {
            s.RemoveAll(typeof(IMediaStore));
            s.AddSingleton<IMediaStore>(media);
        });
        await app.SeedUsersAsync();
        return app;
    }

    private static async Task<byte[]> PngBytesAsync(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        await img.SaveAsync(ms, new PngEncoder());
        return ms.ToArray();
    }

    // Seeds a photo item for the given owner and returns its ExternalId.
    private async Task<string> SeedPhotoAsync(FakeMediaStore media, string ownerId, bool deleted = false)
    {
        string key;
        using (var a = new MemoryStream(await PngBytesAsync(3000, 2000))) key = await media.SaveAsync(a, ".png", CancellationToken.None);
        var photo = new ItemPhoto { MediaPath = key, Order = 0, CreatedAt = DateTimeOffset.UtcNow };
        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), ownerId, DateTimeOffset.UtcNow);
        if (deleted) item.DeletedAt = DateTimeOffset.UtcNow;
        item.Photos.Add(photo);
        await using var db = fx.NewDbContext();
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return photo.ExternalId;
    }

    [Fact]
    public async Task Owner_Display_LazyGenerates_StableImmutableBytes()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var externalId = await SeedPhotoAsync(media, TestUsers.AliceId);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);

        var resp1 = await client.GetAsync($"/media/{externalId}?variant=display");
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal("image/jpeg", resp1.Content.Headers.ContentType!.MediaType);
        Assert.True(resp1.Headers.CacheControl!.Public);
        Assert.Contains("immutable", resp1.Headers.CacheControl.ToString());
        var bytes1 = await resp1.Content.ReadAsByteArrayAsync();

        var resp2 = await client.GetAsync($"/media/{externalId}?variant=display");
        var bytes2 = await resp2.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes1, bytes2); // immutable: same bytes on repeat
    }

    [Fact]
    public async Task Owner_Original_ReturnsFullResOriginal()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var externalId = await SeedPhotoAsync(media, TestUsers.AliceId);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var resp = await client.GetAsync($"/media/{externalId}?variant=original");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var info = await Image.IdentifyAsync(new MemoryStream(await resp.Content.ReadAsByteArrayAsync()));
        Assert.Equal(3000, info.Width); // unresized original
    }

    [Fact]
    public async Task NonOwner_Gets_404()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var externalId = await SeedPhotoAsync(media, TestUsers.AliceId);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.BobId);
        var resp = await client.GetAsync($"/media/{externalId}?variant=display");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownId_Gets_404()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var resp = await client.GetAsync($"/media/nonexistent?variant=display");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SoftDeletedItemsPhoto_Gets_404()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var externalId = await SeedPhotoAsync(media, TestUsers.AliceId, deleted: true);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var resp = await client.GetAsync($"/media/{externalId}?variant=display");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
