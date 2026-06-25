using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class AuthTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", fx.ConnectionString);
        });

    // GET the page, scrape its anti-forgery token, POST the form with the cookie+token.
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, params (string, string)[] fields)
    {
        var getHtml = await client.GetStringAsync(url);
        var token = Regex.Match(getHtml,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
        };
        foreach (var (k, v) in fields) form.Add(new(k, v));
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    private static HttpClient CookieClient(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Register_CreatesUser_AndSignsIn()
    {
        using var app = CreateApp();
        var client = CookieClient(app);

        var resp = await PostFormAsync(client, "/Register",
            ("Input.Email", "alice@example.com"), ("Input.Password", "password1"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.OriginalString);
        Assert.True(resp.Headers.Contains("Set-Cookie")); // auth cookie issued
    }

    [Fact]
    public async Task Login_WithWrongPassword_Fails()
    {
        using var app = CreateApp();
        var client = CookieClient(app);
        await PostFormAsync(client, "/Register",
            ("Input.Email", "bob@example.com"), ("Input.Password", "password1"));

        var fresh = CookieClient(app);
        var resp = await PostFormAsync(fresh, "/Login",
            ("Input.Email", "bob@example.com"), ("Input.Password", "wrong"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // re-renders form
        Assert.Contains("Invalid email or password.", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthenticatedNav_ContainsLogoutFormWithExactlyOneAntiForgeryToken()
    {
        using var app = CreateApp();
        var client = CookieClient(app);

        // Register signs the user in automatically, so the client is now authenticated.
        await PostFormAsync(client, "/Register",
            ("Input.Email", "carol@example.com"), ("Input.Password", "password1"));

        // A freshly registered user is unapproved (Task 3 will auto-approve or admin-approve).
        // The gate redirects them to /Pending; verify the redirect and that /Pending renders
        // with a logout form containing exactly one anti-forgery token.
        var gatedResp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, gatedResp.StatusCode);
        Assert.Equal("/Pending", gatedResp.Headers.Location!.OriginalString);

        var pendingHtml = await client.GetStringAsync("/Pending");

        // The Pending page must include a logout form.
        Assert.Contains("action=\"/Logout\"", pendingHtml);

        // Extract the Pending page's own logout form and verify it has exactly one
        // anti-forgery token (the layout nav-logout form may add another on the page).
        var logoutFormMatch = Regex.Match(pendingHtml,
            @"<form[^>]*action=""/Logout""[^>]*>(.*?)</form>",
            RegexOptions.Singleline);
        Assert.True(logoutFormMatch.Success, "logout form not found on /Pending");
        var tokenCount = Regex.Matches(logoutFormMatch.Groups[1].Value, "__RequestVerificationToken").Count;
        Assert.Equal(1, tokenCount);
    }

    [Fact]
    public async Task Logout_SignsOutUser()
    {
        using var app = CreateApp();
        var client = CookieClient(app);

        // Register signs the user in; the client cookie jar holds the auth cookie.
        var regResp = await PostFormAsync(client, "/Register",
            ("Input.Email", "dave@example.com"), ("Input.Password", "password1"));
        Assert.Equal(HttpStatusCode.Redirect, regResp.StatusCode);

        // A freshly registered user is unapproved, so GET / redirects to /Pending.
        // Scrape the logout form from /Pending instead of from the home page.
        var pendingHtml = await client.GetStringAsync("/Pending");
        var formTag = Regex.Match(pendingHtml, @"<form[^>]*action=""/Logout""[^>]*>").Value;
        Assert.False(string.IsNullOrEmpty(formTag), "logout form not found on /Pending");

        var action = Regex.Match(formTag, @"action=""([^""]*)""").Groups[1].Value;
        // Regression guard: the form must target /Logout.
        Assert.Equal("/Logout", action);

        var token = Regex.Match(pendingHtml,
            @"<form[^>]*action=""/Logout""[^>]*>.*?name=""__RequestVerificationToken""[^>]*value=""([^""]+)""",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "No anti-forgery token found in logout form");

        // Submit the form to the action the browser would actually use.
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var logoutResp = await client.PostAsync(action, form);

        Assert.Equal(HttpStatusCode.Redirect, logoutResp.StatusCode);
        Assert.Equal("/Login", logoutResp.Headers.Location!.OriginalString);

        // End-to-end proof of sign-out: a follow-up authenticated request now
        // bounces to /Login. This fails if the cookie wasn't actually cleared.
        var afterLogout = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        // Location may be absolute (http://localhost/Login?ReturnUrl=%2F) or relative.
        Assert.Contains("/Login", afterLogout.Headers.Location!.OriginalString);
    }

    private WebApplicationFactory<Program> CreateAppWithAdminEmail(string adminEmail) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", fx.ConnectionString);
            b.UseSetting("AdminEmail", adminEmail);
        });

    [Fact]
    public async Task Register_WithAdminEmail_IsApprovedAndInAdminRole()
    {
        var adminEmail = "reg-admin-" + System.Guid.NewGuid().ToString("N") + "@example.com";
        using var app = CreateAppWithAdminEmail(adminEmail);
        var client = CookieClient(app);

        var resp = await PostFormAsync(client, "/Register",
            ("Input.Email", adminEmail), ("Input.Password", "password1"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = await users.FindByEmailAsync(adminEmail);
        Assert.NotNull(u);
        Assert.True(u!.IsApproved);
        Assert.True(await users.IsInRoleAsync(u, "Admin"));
    }

    [Fact]
    public async Task Register_WithNormalEmail_IsUnapprovedAndNotInAdminRole()
    {
        var normalEmail = "reg-normal-" + System.Guid.NewGuid().ToString("N") + "@example.com";
        using var app = CreateAppWithAdminEmail("some-other-admin@example.com");
        var client = CookieClient(app);

        var resp = await PostFormAsync(client, "/Register",
            ("Input.Email", normalEmail), ("Input.Password", "password1"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = await users.FindByEmailAsync(normalEmail);
        Assert.NotNull(u);
        Assert.False(u!.IsApproved);
        Assert.False(await users.IsInRoleAsync(u, "Admin"));
    }

    [Fact]
    public async Task Login_LocksAccount_AfterRepeatedFailures()
    {
        using var app = CreateApp();
        var email = "lockout-" + System.Guid.NewGuid().ToString("N") + "@example.com";

        // Register (auto signs in on a throwaway client).
        await PostFormAsync(CookieClient(app), "/Register",
            ("Input.Email", email), ("Input.Password", "password1"));

        // 10 failed sign-ins (MaxFailedAccessAttempts) on an unauthenticated client.
        var attacker = CookieClient(app);
        for (var i = 0; i < 10; i++)
            await PostFormAsync(attacker, "/Login", ("Input.Email", email), ("Input.Password", "wrong"));

        // The account is now locked per Identity's lockout options.
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True(await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!));
        }

        // Even the correct password is now rejected, with the lockout message.
        var resp = await PostFormAsync(CookieClient(app), "/Login",
            ("Input.Email", email), ("Input.Password", "password1"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("temporarily locked", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DataProtectionKeys_PersistToConfiguredPath()
    {
        var keysDir = Path.Combine(Path.GetTempPath(), "mathom-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keysDir);
        try
        {
            using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Testing");
                b.UseSetting("ConnectionStrings:Mathom", fx.ConnectionString);
                b.UseSetting("DataProtection:KeysPath", keysDir);
            });

            // Rendering a form generates an anti-forgery token, which forces Data
            // Protection to create and persist a key in the configured directory.
            var resp = await app.CreateClient().GetAsync("/Register");
            resp.EnsureSuccessStatusCode();

            Assert.NotEmpty(Directory.GetFiles(keysDir, "key-*.xml"));
        }
        finally
        {
            Directory.Delete(keysDir, recursive: true);
        }
    }
}
