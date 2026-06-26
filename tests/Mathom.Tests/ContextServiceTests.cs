using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Contexts;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ContextServiceTests(PostgresFixture fx)
{
    [Fact]
    public async Task Create_List_Rename_AreUserScoped_AndDedupeCaseInsensitively()
    {
        var u = "ctxsvc-crud";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new ContextService(db);

        var created = await svc.CreateAsync(u, "  Business  ", CancellationToken.None);
        Assert.NotNull(created);
        Assert.Equal("Business", created!.Name); // trimmed

        Assert.Null(await svc.CreateAsync(u, "business", CancellationToken.None)); // ci duplicate
        Assert.Null(await svc.CreateAsync(u, "   ", CancellationToken.None));      // empty

        Assert.True(await svc.RenameAsync(u, created.Id, "Work", CancellationToken.None));
        var list = await svc.ListAsync(u, CancellationToken.None);
        Assert.Equal(new[] { "Work" }, list.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task SetCurrent_RejectsOtherUsersContext_AcceptsNullInbox()
    {
        var a = "ctxsvc-a"; var b = "ctxsvc-b";
        await fx.EnsureUserAsync(a, a + "@example.com");
        await fx.EnsureUserAsync(b, b + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new ContextService(db);

        var aCtx = await svc.CreateAsync(a, "Private", CancellationToken.None);
        Assert.False(await svc.SetCurrentAsync(b, aCtx!.Id, CancellationToken.None)); // not B's
        Assert.True(await svc.SetCurrentAsync(a, aCtx.Id, CancellationToken.None));
        Assert.Equal(aCtx.Id, await svc.GetCurrentAsync(a, CancellationToken.None));
        Assert.True(await svc.SetCurrentAsync(a, null, CancellationToken.None));       // back to Inbox
        Assert.Null(await svc.GetCurrentAsync(a, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_ReassignsItemsToInbox_AndResetsCurrent()
    {
        var u = "ctxsvc-del";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new ContextService(db);

        var ctx = await svc.CreateAsync(u, "Project A", CancellationToken.None);
        await svc.SetCurrentAsync(u, ctx!.Id, CancellationToken.None);
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = "x", CleanText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = u, ContextId = ctx.Id, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();

        Assert.True(await svc.DeleteAsync(u, ctx.Id, CancellationToken.None));

        await using var verify = fx.NewDbContext();
        Assert.Null((await verify.Items.SingleAsync(i => i.Id == item.Id)).ContextId);
        Assert.Null((await verify.Users.SingleAsync(x => x.Id == u)).CurrentContextId);
    }

    [Fact]
    public async Task Delete_RejectsOtherUsersContext()
    {
        var a = "ctxsvc-del-a"; var b = "ctxsvc-del-b";
        await fx.EnsureUserAsync(a, a + "@example.com");
        await fx.EnsureUserAsync(b, b + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new ContextService(db);
        var aCtx = await svc.CreateAsync(a, "Secret", CancellationToken.None);
        Assert.False(await svc.DeleteAsync(b, aCtx!.Id, CancellationToken.None));
    }
}
