using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Mathom.Tests;

// Real-auth host (no TestAuthHandler) for device-token tests, with a controllable clock.
public class DeviceAuthTestApp(string connectionString, Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Mathom", connectionString);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            configureServices?.Invoke(services);
        });
    }

    public async Task<(string Email, string Password, string UserId)> CreateUserAsync(bool approved)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "password1";
        var user = new ApplicationUser { UserName = email, Email = email, IsApproved = approved };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors));
        return (email, password, user.Id);
    }

    public HttpClient Bearer(string accessToken)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}
