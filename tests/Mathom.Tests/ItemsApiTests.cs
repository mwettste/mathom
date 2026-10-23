using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Contexts;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
        Assert.Equal("secret raw transcript", j.GetProperty("rawText").GetString());
        Assert.False(j.TryGetProperty("photoExternalIds", out _));
    }

    [Fact]
    public async Task Item_Detail_TagsAndTranslations_AreOrdered()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var p = "o" + Guid.NewGuid().ToString("N");
        var item = await SeedAsync(alice, i =>
        {
            foreach (var n in new[] { "m", "B", "z", "a" })
                i.ItemTags.Add(new ItemTag { Tag = new Tag { Name = p + n } });
            i.Translations.Add(new ItemTranslation { Locale = "fr", Title = "t", CleanText = "c" });
            i.Translations.Add(new ItemTranslation { Locale = "de", Title = "t", CleanText = "c" });
        });
        var token = await LoginAsync(app, email, password);

        var j = await JsonAsync(await app.Bearer(token).GetAsync($"/api/v1/items/{item.Id}"));

        Assert.Equal(new[] { p + "a", p + "B", p + "m", p + "z" },
            j.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal(new[] { "de", "fr" },
            j.GetProperty("translations").EnumerateArray().Select(t => t.GetProperty("locale").GetString()).ToArray());
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

    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<Item> ReloadAsync(Guid id)
    {
        await using var db = fx.NewDbContext();
        return await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<ContextView> NewContextAsync(string userId, string name)
    {
        await using var db = fx.NewDbContext();
        return (await new ContextService(db).CreateAsync(userId, name, CancellationToken.None))!;
    }

    [Fact]
    public async Task Move_ToContext_SetsContextAndPending()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var ctx = await NewContextAsync(alice, "Work");
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsJsonAsync($"/api/v1/items/{item.Id}/move", new { contextId = ctx.Id });

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var row = await ReloadAsync(item.Id);
        Assert.Equal(ctx.Id, row.ContextId);
        Assert.Equal(ItemStatus.Pending, row.Status);
    }

    [Fact]
    public async Task Move_ExplicitNull_MovesToInbox()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var ctx = await NewContextAsync(alice, "Work");
        var item = await SeedAsync(alice, i => { i.ContextId = ctx.Id; i.Status = ItemStatus.Ready; });
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsync($"/api/v1/items/{item.Id}/move", Raw("{\"contextId\":null}"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var row = await ReloadAsync(item.Id);
        Assert.Null(row.ContextId);
        Assert.Equal(ItemStatus.Pending, row.Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"context_id\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"contxtId\":null}")]
    [InlineData("")]
    public async Task Move_MissingContextId_400_InvalidRequest_NotMoved(string body)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var ctx = await NewContextAsync(alice, "Work");
        var item = await SeedAsync(alice, i => { i.ContextId = ctx.Id; i.Status = ItemStatus.Ready; });
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsync($"/api/v1/items/{item.Id}/move", Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_request", (await JsonAsync(resp)).GetProperty("code").GetString());
        var row = await ReloadAsync(item.Id);
        Assert.Equal(ctx.Id, row.ContextId);
        Assert.Equal(ItemStatus.Ready, row.Status);
    }

    [Fact]
    public async Task Move_OtherUsersContext_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var bobCtx = await NewContextAsync(bob, "Bobs");
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsJsonAsync($"/api/v1/items/{item.Id}/move", new { contextId = bobCtx.Id });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(resp)).GetProperty("code").GetString());
        var row = await ReloadAsync(item.Id);
        Assert.Null(row.ContextId);
        Assert.Equal(ItemStatus.Ready, row.Status);
    }

    [Fact]
    public async Task Move_OtherUsersItem_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var ctx = await NewContextAsync(alice, "Work");
        var item = await SeedAsync(bob, i => i.Status = ItemStatus.Ready);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsJsonAsync($"/api/v1/items/{item.Id}/move", new { contextId = ctx.Id });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(resp)).GetProperty("code").GetString());
        Assert.Null((await ReloadAsync(item.Id)).ContextId);
    }

    [Fact]
    public async Task Delete_SoftDeletes_AndDisappearsFromInbox()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var token = await LoginAsync(app, email, password);
        var client = app.Bearer(token);

        var resp = await client.DeleteAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotNull((await ReloadAsync(item.Id)).DeletedAt);
        var page = await JsonAsync(await client.GetAsync("/api/v1/inbox"));
        Assert.DoesNotContain(page.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == item.Id);
        var detail = await client.GetAsync($"/api/v1/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task Delete_OtherUsersItem_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var item = await SeedAsync(bob);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).DeleteAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(resp)).GetProperty("code").GetString());
        Assert.Null((await ReloadAsync(item.Id)).DeletedAt);
    }

    [Fact]
    public async Task Reprocess_FailedItem_202_Pending()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => { i.Status = ItemStatus.Failed; i.Error = "boom"; });
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsync($"/api/v1/items/{item.Id}/reprocess", null);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var row = await ReloadAsync(item.Id);
        Assert.Equal(ItemStatus.Pending, row.Status);
        Assert.Null(row.Error);
    }

    [Theory]
    [InlineData(ItemStatus.Pending)]
    [InlineData(ItemStatus.Processing)]
    public async Task Reprocess_InFlightItem_409_NotReprocessable(ItemStatus status)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.Status = status);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsync($"/api/v1/items/{item.Id}/reprocess", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("not_reprocessable", (await JsonAsync(resp)).GetProperty("code").GetString());
        Assert.Equal(status, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Reprocess_OtherUsersItem_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var item = await SeedAsync(bob, i => i.Status = ItemStatus.Failed);
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).PostAsync($"/api/v1/items/{item.Id}/reprocess", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(ItemStatus.Failed, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Actions_OnSoftDeletedItem_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => { i.Status = ItemStatus.Failed; i.DeletedAt = Base; });
        var token = await LoginAsync(app, email, password);
        var client = app.Bearer(token);

        var move = await client.PostAsync($"/api/v1/items/{item.Id}/move", Raw("{\"contextId\":null}"));
        var delete = await client.DeleteAsync($"/api/v1/items/{item.Id}");
        var reprocess = await client.PostAsync($"/api/v1/items/{item.Id}/reprocess", null);

        Assert.Equal(HttpStatusCode.NotFound, move.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode); // idempotent retry of own delete
        Assert.Equal(HttpStatusCode.NotFound, reprocess.StatusCode);
        var row = await ReloadAsync(item.Id);
        Assert.Equal(Base, row.DeletedAt);
        Assert.Equal(ItemStatus.Failed, row.Status);
    }

    private const string IsoMillis = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$";

    [Fact]
    public async Task Move_Twice_SecondCallDoesNotRequeue()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var ctx = await NewContextAsync(alice, "Work");
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var first = await client.PostAsJsonAsync($"/api/v1/items/{item.Id}/move", new { contextId = ctx.Id });
        Assert.Equal(ItemStatus.Pending, (await ReloadAsync(item.Id)).Status);
        await using (var db = fx.NewDbContext())
            await db.Items.Where(i => i.Id == item.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, ItemStatus.Ready));
        var second = await client.PostAsJsonAsync($"/api/v1/items/{item.Id}/move", new { contextId = ctx.Id });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(ItemStatus.Ready, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Delete_Twice_BothNoContent()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var first = await client.DeleteAsync($"/api/v1/items/{item.Id}");
        var second = await client.DeleteAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Delete_OtherUsersSoftDeletedItem_404()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var item = await SeedAsync(bob, i => i.DeletedAt = Base);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var resp = await client.DeleteAsync($"/api/v1/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Reprocess_Twice_202ThenConflict()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Failed);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var first = await client.PostAsync($"/api/v1/items/{item.Id}/reprocess", null);
        var second = await client.PostAsync($"/api/v1/items/{item.Id}/reprocess", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("not_reprocessable", (await JsonAsync(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiFallback_NonGuidId_404_NotFound()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var resp = await client.GetAsync("/api/v1/items/abc");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiFallback_UnknownPath_404_NotFound()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var resp = await client.GetAsync("/api/v1/nope");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var j = await JsonAsync(resp);
        Assert.Equal("not_found", j.GetProperty("code").GetString());
        Assert.True(j.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task ApiFallback_WrongMethod_405_MethodNotAllowed()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var resp = await client.PutAsync($"/api/v1/items/{item.Id}", Raw("{}"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        Assert.Equal("method_not_allowed", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiFallback_MoveWithTextPlain_415_UnsupportedMediaType()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var resp = await client.PostAsync($"/api/v1/items/{item.Id}/move",
            new StringContent("{\"contextId\":null}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
        Assert.Equal("unsupported_media_type", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task WebNotFound_IsNotJson()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/does-not-exist");

        Assert.NotEqual("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    public async Task Timestamps_AreUtcWithMilliseconds(int millis)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var created = Base.AddMilliseconds(millis);
        var item = await SeedAsync(alice, i => { i.CreatedAt = created; i.ProcessedAt = created; i.Status = ItemStatus.Ready; });
        var client = app.Bearer(await LoginAsync(app, email, password));

        var detail = await JsonAsync(await client.GetAsync($"/api/v1/items/{item.Id}"));
        var inbox = await JsonAsync(await client.GetAsync("/api/v1/inbox"));

        Assert.Matches(IsoMillis, detail.GetProperty("createdAt").GetString()!);
        Assert.Matches(IsoMillis, detail.GetProperty("processedAt").GetString()!);
        var entry = inbox.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == item.Id);
        Assert.Matches(IsoMillis, entry.GetProperty("createdAt").GetString()!);
        Assert.Equal($"2026-01-01T12:00:00.{millis:000}Z", entry.GetProperty("createdAt").GetString());
    }

    [Theory]
    [InlineData("POST", "move")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "reprocess")]
    public async Task Actions_WatchSession_403(string method, string action)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var item = await SeedAsync(alice, i => i.Status = ItemStatus.Ready);
        var phone = await LoginAsync(app, email, password);
        var watchResp = await app.Bearer(phone).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Watch" });
        var watchToken = (await JsonAsync(watchResp)).GetProperty("accessToken").GetString()!;
        var url = action == "" ? $"/api/v1/items/{item.Id}" : $"/api/v1/items/{item.Id}/{action}";
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (action == "move") req.Content = Raw("{\"contextId\":null}");

        var resp = await app.Bearer(watchToken).SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("forbidden_scope", (await JsonAsync(resp)).GetProperty("code").GetString());
        var row = await ReloadAsync(item.Id);
        Assert.Null(row.DeletedAt);
        Assert.Equal(ItemStatus.Ready, row.Status);
    }
}
