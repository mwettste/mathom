using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PhotoDetailPageTests(PostgresFixture fx)
{
    [Fact]
    public async Task NotePage_RendersPhotoImages()
    {
        var media = new FakeMediaStore();
        var app = new TestWebAppFactory(fx.ConnectionString, s =>
        {
            s.RemoveAll(typeof(IMediaStore));
            s.AddSingleton<IMediaStore>(media);
        });
        await app.SeedUsersAsync();

        string key; using (var a = new MemoryStream(new byte[] { 1 })) key = await media.SaveAsync(a, ".jpg", CancellationToken.None);
        var photoId = Guid.NewGuid();
        var item = Item.CreatePending(SourceType.Photo, "list: milk", Guid.NewGuid().ToString(), TestUsers.AliceId, DateTimeOffset.UtcNow);
        item.Status = ItemStatus.Ready;
        item.CleanText = "list: milk";
        item.Photos.Add(new ItemPhoto { Id = photoId, MediaPath = key, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        await using (var db = fx.NewDbContext()) { db.Items.Add(item); await db.SaveChangesAsync(); }

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.AliceId);
        var html = await client.GetStringAsync($"/Note/{item.Id}");

        Assert.Contains($"/media/{photoId}", html);
    }
}
