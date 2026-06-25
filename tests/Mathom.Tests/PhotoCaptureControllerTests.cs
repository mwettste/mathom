using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PhotoCaptureControllerTests(PostgresFixture fx)
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

    private static MultipartFormDataContent Payload(string idempotencyKey, int count, string contentType = "image/jpeg",
        string fileName = "p.jpg", int bytesEach = 3)
    {
        var form = new MultipartFormDataContent();
        for (var i = 0; i < count; i++)
        {
            var file = new ByteArrayContent(new byte[bytesEach]);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(file, "images", fileName);
        }
        form.Add(new StringContent(idempotencyKey), "idempotencyKey");
        return form;
    }

    [Fact]
    public async Task Post_Photo_CreatesPendingPhotoItem_WithPhotos()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        var resp = await client.PostAsync("/capture/photo", Payload("photo-1", count: 2));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        var item = await db.Items.Include(i => i.Photos).SingleAsync(i => i.IdempotencyKey == "photo-1");
        Assert.Equal(SourceType.Photo, item.SourceType);
        Assert.Equal(ItemStatus.Pending, item.Status);
        Assert.Equal("", item.RawText);
        Assert.Equal(2, item.Photos.Count);
        Assert.Equal(new[] { 0, 1 }, item.Photos.OrderBy(p => p.Order).Select(p => p.Order).ToArray());
        foreach (var p in item.Photos) Assert.True(media.Has(p.MediaPath));
    }

    [Fact]
    public async Task Post_Photo_IsIdempotent()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        await client.PostAsync("/capture/photo", Payload("photo-dup", 1));
        var second = await client.PostAsync("/capture/photo", Payload("photo-dup", 1));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.Equal(1, await db.Items.CountAsync(i => i.IdempotencyKey == "photo-dup"));
    }

    [Fact]
    public async Task Post_Photo_RejectsMissingImages()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        var form = new MultipartFormDataContent { { new StringContent("photo-none"), "idempotencyKey" } };
        var resp = await client.PostAsync("/capture/photo", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Photo_RejectsTooManyImages()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        var resp = await client.PostAsync("/capture/photo", Payload("photo-many", count: 9));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.False(await db.Items.IgnoreQueryFilters().AnyAsync(i => i.IdempotencyKey == "photo-many"));
    }

    [Fact]
    public async Task Post_Photo_RejectsUnsupportedFormat()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        var resp = await client.PostAsync("/capture/photo",
            Payload("photo-heic", count: 1, contentType: "image/heic", fileName: "p.heic"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.False(await db.Items.IgnoreQueryFilters().AnyAsync(i => i.IdempotencyKey == "photo-heic"));
    }
}
