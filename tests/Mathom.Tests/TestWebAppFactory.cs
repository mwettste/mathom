using System;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mathom.Tests;

public class TestWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly Action<IServiceCollection>? _configureServices;

    public TestWebAppFactory(string connectionString, Action<IServiceCollection>? configureServices = null)
    {
        _connectionString = connectionString;
        _configureServices = configureServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Mathom", _connectionString);
        builder.UseSetting("AdminEmail", TestUsers.AdminEmail);

        builder.ConfigureTestServices(services =>
        {
            // Make the Test scheme the default for all auth operations so [Authorize(Roles=...)]
            // challenges/forbids via TestAuthHandler (returns 403) instead of the Identity
            // cookie scheme (which would redirect to /Login).
            services.AddAuthentication(o =>
                {
                    o.DefaultScheme = TestUsers.Scheme;
                    o.DefaultAuthenticateScheme = TestUsers.Scheme;
                    o.DefaultChallengeScheme = TestUsers.Scheme;
                    o.DefaultForbidScheme = TestUsers.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestUsers.Scheme, _ => { });
            services.AddAuthorization(o =>
            {
                o.DefaultPolicy = new AuthorizationPolicyBuilder(TestUsers.Scheme)
                    .RequireAuthenticatedUser().Build();
            });

            _configureServices?.Invoke(services);
        });
    }

    // Seed the fixed users (FK targets) once the host is built.
    public async Task SeedUsersAsync()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await EnsureAsync(users, TestUsers.AliceId, TestUsers.AliceId + "@example.com", true);
        await EnsureAsync(users, TestUsers.BobId, TestUsers.BobId + "@example.com", true);
        await EnsureAsync(users, TestUsers.AdminId, TestUsers.AdminId + "@example.com", true);
        await EnsureAsync(users, TestUsers.PendingId, TestUsers.PendingId + "@example.com", false);

        if (!await roles.RoleExistsAsync("Admin")) await roles.CreateAsync(new IdentityRole("Admin"));
        var admin = await users.FindByIdAsync(TestUsers.AdminId);
        if (admin is not null && !await users.IsInRoleAsync(admin, "Admin")) await users.AddToRoleAsync(admin, "Admin");
    }

    private static async Task EnsureAsync(UserManager<ApplicationUser> users, string id, string email, bool isApproved)
    {
        var existing = await users.FindByIdAsync(id);
        if (existing is not null)
        {
            if (existing.IsApproved != isApproved)
            {
                existing.IsApproved = isApproved;
                await users.UpdateAsync(existing);
            }
            return;
        }
        var user = new ApplicationUser { Id = id, UserName = email, Email = email, IsApproved = isApproved };
        await users.CreateAsync(user, "password1");
    }
}
