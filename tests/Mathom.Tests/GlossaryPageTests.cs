using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class GlossaryPageTests
{
    private readonly PostgresFixture _fx;
    public GlossaryPageTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Add_Then_Page_ShowsTerm()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var page = await client.GetStringAsync("/Glossary");

        var resp = await client.PostAsync("/Glossary?handler=Add", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string,string>("term", "Obersaxen"),
        }));
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Contains("Obersaxen", await resp.Content.ReadAsStringAsync());

        // Persisted for Alice (the default test user).
        await using var db = _fx.NewDbContext();
        Assert.True(await db.GlossaryTerms.AnyAsync(g => g.UserId == TestUsers.AliceId && g.Term == "Obersaxen"));
    }
}
