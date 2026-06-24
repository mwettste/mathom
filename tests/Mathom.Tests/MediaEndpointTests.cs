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
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class MediaEndpointTests
{
    private readonly PostgresFixture _fx;
    public MediaEndpointTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> CreateAppAsync(FakeMediaStore media)
    {
        var app = new TestWebAppFactory(_fx.ConnectionString, s =>
        {
            s.RemoveAll(typeof(IMediaStore));
            s.AddSingleton<IMediaStore>(media);
        });
        await app.SeedUsersAsync();
        return app;
    }

    // Seeds a photo item for the given owner and returns the photo id.
    private async Task<Guid> SeedPhotoAsync(FakeMediaStore media, string ownerId, bool deleted = false)
    {
        string key; using (var a = new MemoryStream(new byte[] { 1, 2, 3 })) key = await media.SaveAsync(a, ".jpg", CancellationToken.None);
        var photoId = Guid.NewGuid();
        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), ownerId, DateTimeOffset.UtcNow);
        if (deleted) item.DeletedAt = DateTimeOffset.UtcNow;
        item.Photos.Add(new ItemPhoto { Id = photoId, MediaPath = key, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        await using var db = _fx.NewDbContext();
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return photoId;
    }

    [Fact]
    public async Task Owner_Gets_Image_Bytes()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var photoId = await SeedPhotoAsync(media, TestUsers.AliceId);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var resp = await client.GetAsync($"/media/{photoId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, (await resp.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task NonOwner_Gets_404()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var photoId = await SeedPhotoAsync(media, TestUsers.AliceId);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.BobId);
        var resp = await client.GetAsync($"/media/{photoId}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownId_Gets_404()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var resp = await client.GetAsync($"/media/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SoftDeletedItemsPhoto_Gets_404()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var photoId = await SeedPhotoAsync(media, TestUsers.AliceId, deleted: true);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var resp = await client.GetAsync($"/media/{photoId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
