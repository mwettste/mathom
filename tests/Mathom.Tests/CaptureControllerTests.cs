using System;
using System.Net;
using System.Net.Http;
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
    public async Task Post_Capture_StampsCurrentContext()
    {
        using var app = await CreateAppAsync();
        var client = app.CreateClient();

        // Make a context for Alice and set it current.
        var ctxId = Guid.NewGuid();
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Mathom.Web.Domain.Context
            {
                Id = ctxId, UserId = TestUsers.AliceId, Name = "Work", CreatedAt = DateTimeOffset.UtcNow,
            });
            var alice = await seed.Users.SingleAsync(u => u.Id == TestUsers.AliceId);
            alice.CurrentContextId = ctxId;
            await seed.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync("/capture", new CaptureRequest("scoped idea", "idem-ctx-1"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "idem-ctx-1");
        Assert.Equal(ctxId, item.ContextId);
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

    private async Task<Guid> SetCurrentContextAsync()
    {
        var ctxId = Guid.NewGuid();
        await using var seed = fx.NewDbContext();
        seed.Contexts.Add(new Mathom.Web.Domain.Context
        {
            Id = ctxId, UserId = TestUsers.AliceId, Name = $"Work-{ctxId:N}", CreatedAt = DateTimeOffset.UtcNow,
        });
        var alice = await seed.Users.SingleAsync(u => u.Id == TestUsers.AliceId);
        alice.CurrentContextId = ctxId;
        await seed.SaveChangesAsync();
        return ctxId;
    }

    private static MultipartFormDataContent VoiceForm(string key, string? target = null, string? capturedAt = null)
    {
        var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mp4");
        form.Add(audio, "audio", "clip.m4a");
        form.Add(new StringContent(key), "idempotencyKey");
        if (target is not null) form.Add(new StringContent(target), "target");
        if (capturedAt is not null) form.Add(new StringContent(capturedAt), "capturedAt");
        return form;
    }

    [Fact]
    public async Task Capture_TargetInbox_IgnoresCurrentContext()
    {
        using var app = await CreateAppAsync();
        await SetCurrentContextAsync();
        var resp = await app.CreateClient().PostAsync("/capture/voice", VoiceForm("t-inbox", target: "Inbox"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.Null((await db.Items.SingleAsync(i => i.IdempotencyKey == "t-inbox")).ContextId);
    }

    [Fact]
    public async Task Capture_NoTarget_UsesCurrentContext()
    {
        using var app = await CreateAppAsync();
        var ctxId = await SetCurrentContextAsync();
        var resp = await app.CreateClient().PostAsync("/capture/voice", VoiceForm("t-none"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.Equal(ctxId, (await db.Items.SingleAsync(i => i.IdempotencyKey == "t-none")).ContextId);
    }

    [Fact]
    public async Task Capture_UnknownTarget_400_InvalidTarget()
    {
        using var app = await CreateAppAsync();
        var client = app.CreateClient();

        var voice = await client.PostAsync("/capture/voice", VoiceForm("t-bad-v", target: "elsewhere"));
        Assert.Equal(HttpStatusCode.BadRequest, voice.StatusCode);
        Assert.Equal("invalid_target", (await voice.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());

        var text = await client.PostAsJsonAsync("/capture", new CaptureRequest("x", "t-bad-t", "elsewhere"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.False(await db.Items.IgnoreQueryFilters().AnyAsync(i => i.IdempotencyKey.StartsWith("t-bad-")));
    }

    [Fact]
    public async Task Voice_CapturedAt_TwoHoursAgo_IsCreatedAt()
    {
        using var app = await CreateAppAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        var resp = await app.CreateClient().PostAsync("/capture/voice",
            VoiceForm("c-2h", capturedAt: at.ToString("O")));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "c-2h");
        Assert.True((item.CreatedAt - at).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Voice_CapturedAt_InFuture_UsesNow()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().PostAsync("/capture/voice",
            VoiceForm("c-future", capturedAt: DateTimeOffset.UtcNow.AddHours(3).ToString("O")));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "c-future");
        Assert.True((item.CreatedAt - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Voice_CapturedAt_31DaysAgo_UsesNow()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().PostAsync("/capture/voice",
            VoiceForm("c-old", capturedAt: DateTimeOffset.UtcNow.AddDays(-31).ToString("O")));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "c-old");
        Assert.True((item.CreatedAt - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(1));
    }
}
