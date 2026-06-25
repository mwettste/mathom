using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Mathom.Web.Capture;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

file record IdResponse(Guid Id);

[Collection("postgres")]
public class CaptureControllerTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> CreateAppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    [Fact]
    public async Task Post_Capture_CreatesPendingItem()
    {
        using var app = await CreateAppAsync();
        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/capture", new CaptureRequest("a fresh idea", "idem-1"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<IdResponse>();

        await using var db = fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "idem-1");
        Assert.Equal(item.Id, body!.Id);
        Assert.Equal(ItemStatus.Pending, item.Status);
        Assert.Equal("a fresh idea", item.RawText);
        Assert.Equal(SourceType.Text, item.SourceType);
    }

    [Fact]
    public async Task Post_Capture_IsIdempotent()
    {
        using var app = await CreateAppAsync();
        var client = app.CreateClient();

        var first = await client.PostAsJsonAsync("/capture", new CaptureRequest("dup", "idem-dup"));
        var firstBody = await first.Content.ReadFromJsonAsync<IdResponse>();

        var second = await client.PostAsJsonAsync("/capture", new CaptureRequest("dup", "idem-dup"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<IdResponse>();
        Assert.Equal(firstBody!.Id, secondBody!.Id);

        await using var db = fx.NewDbContext();
        Assert.Equal(1, await db.Items.CountAsync(i => i.IdempotencyKey == "idem-dup"));
    }

    [Fact]
    public async Task Post_Capture_RejectsEmptyText()
    {
        using var app = await CreateAppAsync();
        var client = app.CreateClient();
        var resp = await client.PostAsJsonAsync("/capture", new CaptureRequest("   ", "idem-empty"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Capture_RejectsOverlongText()
    {
        using var app = await CreateAppAsync();
        var client = app.CreateClient();
        var huge = new string('a', 100_001); // just over the 100k cap
        var resp = await client.PostAsJsonAsync("/capture", new CaptureRequest(huge, "idem-huge"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.False(await db.Items.IgnoreQueryFilters().AnyAsync(i => i.IdempotencyKey == "idem-huge"));
    }

    [Fact]
    public async Task Post_Capture_IdempotencyKey_ReusedAfterSoftDelete_ReturnsOriginalId()
    {
        const string key = "reuse-key-after-delete";
        using var app = await CreateAppAsync();
        var client = app.CreateClient();

        // 1. First capture — creates the item.
        var first = await client.PostAsJsonAsync("/capture", new CaptureRequest("original text", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<IdResponse>();

        // 2. Soft-delete the item directly via DbContext (bypasses the global query filter).
        await using var db = fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == key);
        item.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // 3. POST again with the same idempotency key.
        var second = await client.PostAsJsonAsync("/capture", new CaptureRequest("any text", key));

        // 4. Must NOT be a 500; must return the original item's id.
        Assert.True(
            second.StatusCode == HttpStatusCode.OK || second.StatusCode == HttpStatusCode.Created,
            $"Expected 200 or 201 but got {(int)second.StatusCode}");
        var secondBody = await second.Content.ReadFromJsonAsync<IdResponse>();
        Assert.Equal(firstBody!.Id, secondBody!.Id);
    }
}
