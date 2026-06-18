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

        var html = await client.GetStringAsync("/");

        // The logout form must exist.
        Assert.Contains("nav-logout", html);

        // Count __RequestVerificationToken hidden inputs inside the logout form block.
        // We extract the form element from nav-logout to isolate it from any other forms on the page.
        var logoutFormMatch = Regex.Match(html,
            @"<form[^>]*class=""nav-logout""[^>]*>(.*?)</form>",
            RegexOptions.Singleline);
        Assert.True(logoutFormMatch.Success, "nav-logout form not found in HTML");

        var formBody = logoutFormMatch.Groups[1].Value;
        var tokenCount = Regex.Matches(formBody, "__RequestVerificationToken").Count;

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

        // GET the home page while authenticated to scrape the anti-forgery token
        // that is embedded in the logout form by the asp-page tag helper.
        var homeHtml = await client.GetStringAsync("/");
        var token = Regex.Match(homeHtml,
            @"<form[^>]*class=""nav-logout""[^>]*>.*?name=""__RequestVerificationToken""[^>]*value=""([^""]+)""",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "No anti-forgery token found in logout form");

        // POST to /Logout with the scraped token.
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var logoutResp = await client.PostAsync("/Logout", form);

        // Must redirect to /Login.
        Assert.Equal(HttpStatusCode.Redirect, logoutResp.StatusCode);
        Assert.Equal("/Login", logoutResp.Headers.Location!.OriginalString);

        // The Identity auth cookie must be cleared/expired in the response.
        var setCookieHeaders = logoutResp.Headers.GetValues("Set-Cookie");
        var identityCookieCleared = setCookieHeaders.Any(h =>
            h.Contains(".AspNetCore.Identity.Application") &&
            (h.Contains("expires=Thu, 01 Jan 1970") ||
             h.Contains("expires=Mon, 01 Jan 0001") ||
             h.Contains("max-age=0") ||
             // Empty value means the cookie is being cleared
             Regex.IsMatch(h, @"\.AspNetCore\.Identity\.Application=;")));
        Assert.True(identityCookieCleared, $"Identity cookie was not cleared. Set-Cookie headers: {string.Join("; ", setCookieHeaders)}");
    }
}
