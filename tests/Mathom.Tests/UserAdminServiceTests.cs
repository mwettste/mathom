using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Admin;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class UserAdminServiceTests(PostgresFixture fx)
{
    private async Task<string> SeedUserAsync(string suffix, bool approved)
    {
        var id = "ua-" + suffix + "-" + Guid.NewGuid().ToString("N");
        await using var db = fx.NewDbContext();
        db.Users.Add(new ApplicationUser { Id = id, UserName = id + "@example.com", Email = id + "@example.com", IsApproved = approved });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Approve_Revoke_And_IsApproved()
    {
        var pending = await SeedUserAsync("p", approved: false);
        await using var db = fx.NewDbContext();
        var svc = new UserAdminService(db);

        Assert.False(await svc.IsApprovedAsync(pending, CancellationToken.None));
        Assert.True(await svc.ApproveAsync(pending, CancellationToken.None));
        Assert.True(await svc.IsApprovedAsync(pending, CancellationToken.None));

        Assert.True(await svc.RevokeAsync("some-admin", pending, CancellationToken.None));
        Assert.False(await svc.IsApprovedAsync(pending, CancellationToken.None));

        Assert.False(await svc.IsApprovedAsync("does-not-exist", CancellationToken.None)); // missing → false
    }

    [Fact]
    public async Task Revoke_Self_IsRejected()
    {
        var admin = await SeedUserAsync("self", approved: true);
        await using var db = fx.NewDbContext();
        var svc = new UserAdminService(db);

        Assert.False(await svc.RevokeAsync(admin, admin, CancellationToken.None));   // acting == target
        Assert.True(await svc.IsApprovedAsync(admin, CancellationToken.None));         // unchanged
    }

    [Fact]
    public async Task ListUsers_PutsPendingFirst()
    {
        var approved = await SeedUserAsync("aaa-approved", approved: true);
        var pending = await SeedUserAsync("zzz-pending", approved: false);
        await using var db = fx.NewDbContext();
        var rows = await new UserAdminService(db).ListUsersAsync(CancellationToken.None);

        var iPending = rows.ToList().FindIndex(r => r.Id == pending);
        var iApproved = rows.ToList().FindIndex(r => r.Id == approved);
        Assert.True(iPending >= 0 && iApproved >= 0 && iPending < iApproved); // pending before approved
    }
}
