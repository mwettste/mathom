using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class AuthTests
{
    private readonly PostgresFixture _fx;
    public AuthTests(PostgresFixture fx) => _fx = fx;

    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
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
                b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
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
