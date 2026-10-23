using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Notes;
using Mathom.Web.Media;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class MoveContextTests(PostgresFixture fx)
{
    [Fact]
    public async Task Move_ChangesContext_AndRequeuesForProcessing()
    {
        var u = "move-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var ctxId = Guid.NewGuid();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "x", CleanText = "x", Title = "T", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = u, ContextId = null, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Context { Id = ctxId, UserId = u, Name = "Biz", CreatedAt = DateTimeOffset.UtcNow });
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        await using var db = fx.NewDbContext();
        var svc = new NoteService(db, new FakeMediaStore());
        Assert.True(await svc.MoveAsync(u, item.Id, ctxId, CancellationToken.None));

        await using var verify = fx.NewDbContext();
        var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ctxId, loaded.ContextId);
        Assert.Equal(ItemStatus.Pending, loaded.Status); // re-queued
    }

    [Fact]
    public async Task Move_ToCurrentContext_DoesNotRequeue()
    {
        var u = "move-same-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var ctxId = Guid.NewGuid();
        var inboxItem = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = u, ContextId = null, CreatedAt = DateTimeOffset.UtcNow,
        };
        var ctxItem = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "y", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = u, ContextId = ctxId, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Context { Id = ctxId, UserId = u, Name = "Biz", CreatedAt = DateTimeOffset.UtcNow });
            seed.Items.AddRange(inboxItem, ctxItem);
            await seed.SaveChangesAsync();
        }

        await using var db = fx.NewDbContext();
        var svc = new NoteService(db, new FakeMediaStore());
        Assert.True(await svc.MoveAsync(u, inboxItem.Id, null, CancellationToken.None));
        Assert.True(await svc.MoveAsync(u, ctxItem.Id, ctxId, CancellationToken.None));

        await using var verify = fx.NewDbContext();
        Assert.Equal(ItemStatus.Ready, (await verify.Items.SingleAsync(i => i.Id == inboxItem.Id)).Status);
        Assert.Equal(ItemStatus.Ready, (await verify.Items.SingleAsync(i => i.Id == ctxItem.Id)).Status);
    }

    [Fact]
    public async Task Move_RejectsContextNotOwnedByUser()
    {
        var u = "move-owner"; var other = "move-other";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await fx.EnsureUserAsync(other, other + "@example.com");
        var otherCtx = Guid.NewGuid();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "x", CleanText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = u, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Context { Id = otherCtx, UserId = other, Name = "NotYours", CreatedAt = DateTimeOffset.UtcNow });
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        await using var db = fx.NewDbContext();
        var svc = new NoteService(db, new FakeMediaStore());
        Assert.False(await svc.MoveAsync(u, item.Id, otherCtx, CancellationToken.None));
    }

    [Fact]
    public async Task NotePage_MoveHandler_MovesItem()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var client = app.CreateClient();

        var ctxId = Guid.NewGuid();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "x", CleanText = "x", Title = "T", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = TestUsers.AliceId, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Context { Id = ctxId, UserId = TestUsers.AliceId, Name = "Biz", CreatedAt = DateTimeOffset.UtcNow });
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var page = await client.GetStringAsync($"/Note/{item.Id}");
        var token = System.Text.RegularExpressions.Regex.Match(
            page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var resp = await client.PostAsync($"/Note/{item.Id}?handler=Move", new System.Net.Http.FormUrlEncodedContent(
            new[]
            {
                new System.Collections.Generic.KeyValuePair<string,string>("__RequestVerificationToken", token),
                new System.Collections.Generic.KeyValuePair<string,string>("id", item.Id.ToString()),
                new System.Collections.Generic.KeyValuePair<string,string>("contextId", ctxId.ToString()),
            }));
        Assert.True(resp.IsSuccessStatusCode);

        await using var db = fx.NewDbContext();
        Assert.Equal(ctxId, (await db.Items.SingleAsync(i => i.Id == item.Id)).ContextId);
    }

    private async Task<(string U, Guid CtxId, Guid OtherId, Item Item)> SeedSuggestedAsync(bool inContext)
    {
        var u = "move-sugg-" + Guid.NewGuid().ToString("N");
        await fx.EnsureUserAsync(u, u + "@example.com");
        var ctxId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "x", IdempotencyKey = Guid.NewGuid().ToString(), UserId = u,
            ContextId = inContext ? ctxId : null, SuggestedContextId = otherId, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var seed = fx.NewDbContext();
        seed.Contexts.Add(new Context { Id = ctxId, UserId = u, Name = "Biz", CreatedAt = DateTimeOffset.UtcNow });
        seed.Contexts.Add(new Context { Id = otherId, UserId = u, Name = "Other", CreatedAt = DateTimeOffset.UtcNow });
        seed.Items.Add(item);
        await seed.SaveChangesAsync();
        return (u, ctxId, otherId, item);
    }

    [Fact]
    public async Task Move_ClearsSuggestedContext()
    {
        var (u, ctxId, _, item) = await SeedSuggestedAsync(inContext: false);
        await using var db = fx.NewDbContext();
        Assert.True(await new NoteService(db, new FakeMediaStore()).MoveAsync(u, item.Id, ctxId, CancellationToken.None));

        await using var verify = fx.NewDbContext();
        var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ctxId, loaded.ContextId);
        Assert.Null(loaded.SuggestedContextId);
    }

    [Fact]
    public async Task Move_ToCurrentContext_KeepsSuggestedContext()
    {
        var (u, ctxId, otherId, item) = await SeedSuggestedAsync(inContext: true);
        await using var db = fx.NewDbContext();
        Assert.True(await new NoteService(db, new FakeMediaStore()).MoveAsync(u, item.Id, ctxId, CancellationToken.None));

        await using var verify = fx.NewDbContext();
        var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(otherId, loaded.SuggestedContextId);
    }

    [Fact]
    public async Task DeleteContext_SetsSuggestedContextNull()
    {
        var (_, _, otherId, item) = await SeedSuggestedAsync(inContext: false);
        await using (var db = fx.NewDbContext())
        {
            db.Contexts.Remove(await db.Contexts.SingleAsync(c => c.Id == otherId));
            await db.SaveChangesAsync();
        }
        await using var verify = fx.NewDbContext();
        var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Null(loaded.SuggestedContextId);
    }
}
