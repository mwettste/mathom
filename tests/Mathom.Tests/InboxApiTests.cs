using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Contexts;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class InboxApiTests(PostgresFixture fx)
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    internal static async Task<string> LoginAsync(DeviceAuthTestApp app, string email, string password)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password, deviceName = "iPhone", deviceKind = "phone" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await JsonAsync(resp)).GetProperty("accessToken").GetString()!;
    }

    private async Task<Item> SeedAsync(string userId, DateTimeOffset createdAt, Action<Item>? tweak = null)
    {
        var item = Item.CreatePending(SourceType.Text, "raw", Guid.NewGuid().ToString("N"), userId, createdAt);
        tweak?.Invoke(item);
        await using var db = fx.NewDbContext();
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Inbox_ListsOnlyOwnInboxItems_NewestFirst()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var (_, _, bob) = await app.CreateUserAsync(true);
        var older = await SeedAsync(alice, Base);
        var newer = await SeedAsync(alice, Base.AddMinutes(5));
        await SeedAsync(bob, Base.AddMinutes(1));
        await SeedAsync(alice, Base.AddMinutes(2), i => i.DeletedAt = Base);
        await using (var db = fx.NewDbContext())
        {
            var ctx = await new ContextService(db).CreateAsync(alice, "Work", CancellationToken.None);
            await SeedAsync(alice, Base.AddMinutes(3), i => i.ContextId = ctx!.Id);
        }
        var token = await LoginAsync(app, email, password);

        var resp = await app.Bearer(token).GetAsync("/api/v1/inbox");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { newer.Id, older.Id }, Ids(await JsonAsync(resp)));
    }

    [Fact]
    public async Task Inbox_IncludesPendingProcessingAndFailed()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        await SeedAsync(alice, Base, i => i.Status = ItemStatus.Pending);
        await SeedAsync(alice, Base.AddMinutes(1), i => i.Status = ItemStatus.Processing);
        await SeedAsync(alice, Base.AddMinutes(2), i => i.Status = ItemStatus.Failed);
        var token = await LoginAsync(app, email, password);

        var page = await JsonAsync(await app.Bearer(token).GetAsync("/api/v1/inbox"));

        var statuses = page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("status").GetString()).ToList();
        Assert.Equal(new[] { "failed", "processing", "pending" }, statuses);
        Assert.Equal("text", page.GetProperty("items")[0].GetProperty("sourceType").GetString());
    }

    [Fact]
    public async Task Inbox_Snippet_IsCleanTextOrRawText_Max200()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var ready = await SeedAsync(alice, Base.AddMinutes(2), i =>
        {
            i.Status = ItemStatus.Ready;
            i.CleanText = new string('c', 500);
            i.Title = "T";
        });
        var pendingText = await SeedAsync(alice, Base.AddMinutes(1), i => i.RawText = "hello raw");
        var voice = await SeedAsync(alice, Base, i => { i.SourceType = SourceType.Voice; i.RawText = ""; });
        var token = await LoginAsync(app, email, password);

        var page = await JsonAsync(await app.Bearer(token).GetAsync("/api/v1/inbox"));

        var byId = page.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetGuid());
        Assert.Equal(new string('c', 200), byId[ready.Id].GetProperty("snippet").GetString());
        Assert.Equal("T", byId[ready.Id].GetProperty("title").GetString());
        Assert.Equal("hello raw", byId[pendingText.Id].GetProperty("snippet").GetString());
        Assert.Equal(JsonValueKind.Null, byId[voice.Id].GetProperty("snippet").ValueKind);
        Assert.Equal("voice", byId[voice.Id].GetProperty("sourceType").GetString());
    }

    [Fact]
    public async Task Inbox_Paginates_WithNextCursor()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var all = new List<Guid>();
        for (var n = 0; n < 5; n++) all.Add((await SeedAsync(alice, Base.AddMinutes(n))).Id);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var seen = new List<Guid>();
        var sizes = new List<int>();
        string? cursor = null;
        JsonElement page;
        do
        {
            var url = "/api/v1/inbox?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            page = await JsonAsync(await client.GetAsync(url));
            var ids = Ids(page);
            sizes.Add(ids.Count);
            seen.AddRange(ids);
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(new[] { 2, 2, 1 }, sizes);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(all.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task Inbox_SameCreatedAtAcrossPageBoundary_NoDuplicatesOrGaps()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        var expected = new List<Guid>();
        for (var n = 0; n < 3; n++) expected.Add((await SeedAsync(alice, Base)).Id);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var p1 = await JsonAsync(await client.GetAsync("/api/v1/inbox?limit=2"));
        var cursor = p1.GetProperty("nextCursor").GetString()!;
        var p2 = await JsonAsync(await client.GetAsync("/api/v1/inbox?limit=2&cursor=" + Uri.EscapeDataString(cursor)));

        var seen = Ids(p1).Concat(Ids(p2)).ToList();
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("eDp5")] // base64url of "x:y"
    public async Task Inbox_GarbageCursor_400_InvalidCursor(string cursor)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var client = app.Bearer(await LoginAsync(app, email, password));

        var resp = await client.GetAsync("/api/v1/inbox?cursor=" + cursor);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_cursor", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Inbox_LimitOutOfRange_IsClamped()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, alice) = await app.CreateUserAsync(true);
        for (var n = 0; n < 105; n++) await SeedAsync(alice, Base.AddSeconds(n));
        var client = app.Bearer(await LoginAsync(app, email, password));

        var low = await JsonAsync(await client.GetAsync("/api/v1/inbox?limit=0"));
        var high = await JsonAsync(await client.GetAsync("/api/v1/inbox?limit=500"));

        Assert.Single(Ids(low));
        Assert.Equal(100, Ids(high).Count);
    }

    [Fact]
    public async Task Inbox_WatchSession_403_ForbiddenScope()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(true);
        var phone = await LoginAsync(app, email, password);
        var watchResp = await app.Bearer(phone).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Watch" });
        var watchToken = (await JsonAsync(watchResp)).GetProperty("accessToken").GetString()!;

        var resp = await app.Bearer(watchToken).GetAsync("/api/v1/inbox");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("forbidden_scope", (await JsonAsync(resp)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Inbox_NoToken_401()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/api/v1/inbox");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
