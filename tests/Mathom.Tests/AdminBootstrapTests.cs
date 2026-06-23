using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Admin;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class AdminBootstrapTests
{
    private readonly PostgresFixture _fx;
    public AdminBootstrapTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task EnsureRoleAndPromote_PromotesConfiguredAdmin_AndIsIdempotent()
    {
        using var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var email = "boot-admin-" + System.Guid.NewGuid().ToString("N") + "@example.com";
        await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, IsApproved = false }, "password1");

        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, email, CancellationToken.None);
        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, email, CancellationToken.None); // idempotent

        var u = await users.FindByEmailAsync(email);
        Assert.NotNull(u);
        Assert.True(u!.IsApproved);
        Assert.True(await users.IsInRoleAsync(u, "Admin"));
    }

    [Fact]
    public async Task EnsureRoleAndPromote_NoAdminEmail_JustEnsuresRole()
    {
        using var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, null, CancellationToken.None);
        Assert.True(await roles.RoleExistsAsync("Admin"));
    }
}
