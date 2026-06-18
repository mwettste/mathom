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
}
