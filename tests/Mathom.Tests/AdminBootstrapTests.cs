using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Admin;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class AdminBootstrapTests(PostgresFixture fx)
{
    [Fact]
    public async Task EnsureRoleAndPromote_PromotesConfiguredAdmin_AndIsIdempotent()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var email = "boot-admin-" + System.Guid.NewGuid().ToString("N") + "@example.com";
        await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, IsApproved = false }, "password1");

        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, new[] { email }, CancellationToken.None);
        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, new[] { email }, CancellationToken.None); // idempotent

        var u = await users.FindByEmailAsync(email);
        Assert.NotNull(u);
        Assert.True(u!.IsApproved);
        Assert.True(await users.IsInRoleAsync(u, "Admin"));
    }

    [Fact]
    public async Task EnsureRoleAndPromote_NoAdminEmail_JustEnsuresRole()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, System.Array.Empty<string>(), CancellationToken.None);
        Assert.True(await roles.RoleExistsAsync("Admin"));
    }

    [Fact]
    public async Task EnsureRoleAndPromote_PromotesEveryEmailInList()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var e1 = "list-a-" + System.Guid.NewGuid().ToString("N") + "@example.com";
        var e2 = "list-b-" + System.Guid.NewGuid().ToString("N") + "@example.com";
        await users.CreateAsync(new ApplicationUser { UserName = e1, Email = e1, IsApproved = false }, "password1");
        await users.CreateAsync(new ApplicationUser { UserName = e2, Email = e2, IsApproved = false }, "password1");

        await AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, new[] { e1, e2 }, CancellationToken.None);

        foreach (var e in new[] { e1, e2 })
        {
            var u = await users.FindByEmailAsync(e);
            Assert.NotNull(u);
            Assert.True(u!.IsApproved);
            Assert.True(await users.IsInRoleAsync(u, "Admin"));
        }
    }

    [Fact]
    public void AdminEmailsFromConfig_MergesTrimsAndDedupes()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminEmail"] = "boss@x.test",
                ["PreviewAdminEmails"] = " a@x.test , boss@x.test ,, b@x.test ",
            }).Build();

        var result = AdminBootstrap.AdminEmailsFromConfig(config);

        Assert.Equal(new[] { "boss@x.test", "a@x.test", "b@x.test" }, result);
    }

    [Fact]
    public void AdminEmailsFromConfig_NoPreviewKey_ReturnsJustAdminEmail()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AdminEmail"] = "boss@x.test" }).Build();

        Assert.Equal(new[] { "boss@x.test" }, AdminBootstrap.AdminEmailsFromConfig(config));
    }
}
