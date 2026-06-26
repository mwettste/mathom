using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class SettingsPageTests(PostgresFixture fx)
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Settings_AddLanguage_ReturnsListPartial_WithLocale()
    {
        await using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var client = app.CreateClient();

        var page = await client.GetStringAsync("/Settings");
        var resp = await client.PostAsync("/Settings?handler=Add", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string, string>("locale", "de-CH"),
        }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("German (Switzerland)", html);
    }
}
