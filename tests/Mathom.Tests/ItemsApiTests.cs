using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Contexts;
using Mathom.Web.Domain;
using Xunit;
using static Mathom.Tests.InboxApiTests;

namespace Mathom.Tests;

[Collection("postgres")]
public class ItemsApiTests(PostgresFixture fx)
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<Item> SeedAsync(string userId, Action<Item>? tweak = null, SourceType source = SourceType.Text)
    {
        var item = Item.CreatePending(source, "secret raw transcript", Guid.NewGuid().ToString("N"), userId, Base);
        tweak?.Invoke(item);
        await using var db = fx.NewDbContext();
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Item_Detail_ReturnsFields()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        ContextView ctx;
        await using (var db = fx.NewDbContext())
            ctx = (await new ContextService(db).CreateAsync(alice, "Work", CancellationToken.None))!;
        var tag = "t" + Guid.NewGuid().ToString("N");
        var item = await SeedAsync(alice, i =>
        {
            i.Status = ItemStatus.Ready;
            i.Title = "Title";
            i.CleanText = "Clean";
            i.ItemType = ItemType.Idea;
            i.Actionable = true;
            i.SourceLanguage = "en";
            i.ContextId = ctx.Id;
            i.ProcessedAt = Base.AddMinutes(1);
            i.ItemTags.Add(new ItemTag { Tag = new Tag { Name = tag } });
            i.Translations.Add(new ItemTranslation { Locale = "de", Title = "Titel", CleanText = "Sauber" });
        });
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).GetAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var j = await JsonAsync(resp);
        Assert.Equal(item.Id, j.GetProperty("id").GetGuid());
        Assert.Equal("ready", j.GetProperty("status").GetString());
        Assert.Equal("text", j.GetProperty("sourceType").GetString());
        Assert.Equal("idea", j.GetProperty("type").GetString());
        Assert.True(j.GetProperty("actionable").GetBoolean());
        Assert.Equal("Title", j.GetProperty("title").GetString());
        Assert.Equal("Clean", j.GetProperty("cleanText").GetString());
        Assert.Equal("en", j.GetProperty("sourceLanguage").GetString());
        Assert.Equal(new[] { tag }, j.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        var tr = j.GetProperty("translations")[0];
        Assert.Equal("de", tr.GetProperty("locale").GetString());
        Assert.Equal("Titel", tr.GetProperty("title").GetString());
        Assert.Equal("Sauber", tr.GetProperty("cleanText").GetString());
        Assert.Equal(ctx.Id, j.GetProperty("context").GetProperty("id").GetGuid());
        Assert.Equal("Work", j.GetProperty("context").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("error").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, j.GetProperty("processedAt").ValueKind);
        Assert.False(j.TryGetProperty("rawText", out _));
        Assert.DoesNotContain("secret raw transcript", j.GetRawText());
        Assert.False(j.TryGetProperty("photoExternalIds", out _));
    }

    [Fact]
    public async Task Item_Detail_InboxItem_HasNullContext()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice);
        var token = await LoginAsync(app, email, password);

        var j = await JsonAsync(await app.Bearer(token).GetAsync($"/api/v1/items/{item.Id}"));

        Assert.Equal(JsonValueKind.Null, j.GetProperty("context").ValueKind);
    }

    [Fact]
    public async Task Item_Detail_OtherUsersItem_404_NotFound()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var item = await SeedAsync(bob);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).GetAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Item_Detail_SoftDeleted_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.DeletedAt = Base);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).GetAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Item_Detail_PendingVoice_HasNullTitleAndText()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.RawText = "", SourceType.Voice);
        var token = await LoginAsync(app, email, password);

        var j = await JsonAsync(await app.Bearer(token).GetAsync($"/api/v1/items/{item.Id}"));

        Assert.Equal("pending", j.GetProperty("status").GetString());
        Assert.Equal("voice", j.GetProperty("sourceType").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("cleanText").ValueKind);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("type").ValueKind);
        Assert.Equal(0, j.GetProperty("tags").GetArrayLength());
        Assert.Equal(0, j.GetProperty("translations").GetArrayLength());
    }

    [Fact]
    public async Task Contexts_ListsOwnContexts_InServiceOrder()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        await using (var db = fx.NewDbContext())
        {
            var svc = new ContextService(db);
            await svc.CreateAsync(alice, "Zeta", CancellationToken.None);
            await svc.CreateAsync(alice, "Alpha", CancellationToken.None);
            await svc.CreateAsync(bob, "BobsContext", CancellationToken.None);
        }
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).GetAsync("/api/v1/contexts");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await JsonAsync(resp);
        Assert.Equal(new[] { "Alpha", "Zeta" }, arr.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray());
        Assert.All(arr.EnumerateArray(), c => Assert.NotEqual(Guid.Empty, c.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("/api/v1/contexts")]
    [InlineData("/api/v1/items/00000000-0000-0000-0000-000000000001")]
    public async Task Item_Detail_And_Contexts_WatchSession_403(string path)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var phone = await LoginAsync(app, email, password);
        var watchResp = await app.Bearer(phone).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Watch" });
        var watchToken = (await JsonAsync(watchResp)).GetProperty("accessToken").GetString()!;

        var resp = await app.Bearer(watchToken).GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("forbidden_scope", (await JsonAsync(resp)).GetProperty("code").GetString());
    }
}
