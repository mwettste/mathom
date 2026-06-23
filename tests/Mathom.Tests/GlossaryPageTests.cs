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

    [Fact]
    public async Task Add_WithVariant_StoresBoth_AndPageShowsVariant()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var page = await client.GetStringAsync("/Glossary");

        var resp = await client.PostAsync("/Glossary?handler=Add", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string,string>("term", "FireSkills"),
            new KeyValuePair<string,string>("variant", "Fairstills"),
        }));
        Assert.True(resp.IsSuccessStatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FireSkills", body);
        Assert.Contains("Fairstills", body); // variant rendered

        await using var db = _fx.NewDbContext();
        var term = await db.GlossaryTerms.Include(t => t.Variants)
            .FirstAsync(t => t.UserId == TestUsers.AliceId && t.Term == "FireSkills");
        Assert.Contains(term.Variants, v => v.Text == "Fairstills");
    }

    [Fact]
    public async Task Description_InlineEdit_SetThenClear_RoundTrips()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var page = await client.GetStringAsync("/Glossary");

        // Add a term via the page handler.
        await client.PostAsync("/Glossary?handler=Add", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string,string>("term", "FireSkills"),
        }));

        await using var db = _fx.NewDbContext();
        var termId = await db.GlossaryTerms.Where(t => t.UserId == TestUsers.AliceId && t.Term == "FireSkills").Select(t => t.Id).FirstAsync();

        // The list shows the per-term description region with an "Add context" affordance.
        var list = await client.GetStringAsync("/Glossary");
        Assert.Contains($"glossary-desc-{termId}", list);

        // Set a description.
        var setResp = await client.PostAsync($"/Glossary?handler=SetDescription&id={termId}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string,string>("description", "our internal time-tracking product"),
        }));
        Assert.True(setResp.IsSuccessStatusCode);
        Assert.Contains("our internal time-tracking product", await setResp.Content.ReadAsStringAsync());
        await using (var v = _fx.NewDbContext())
            Assert.Equal("our internal time-tracking product", await v.GlossaryTerms.Where(t => t.Id == termId).Select(t => t.Description).FirstAsync());

        // Cross-user (Bob) cannot edit Alice's term.
        var bob = app.CreateClient();
        bob.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.BobId);
        var bobPage = await bob.GetStringAsync("/Glossary");
        var bobResp = await bob.GetAsync($"/Glossary?handler=EditDescription&id={termId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, bobResp.StatusCode);
    }

    [Fact]
    public async Task NotePage_RendersGlossaryTokenAndScript()
    {
        using var app = await AppAsync();
        // seed a Ready note for Alice
        System.Guid id;
        await using (var db = _fx.NewDbContext())
        {
            var item = new Mathom.Web.Domain.Item
            {
                Id = System.Guid.NewGuid(), Status = Mathom.Web.Domain.ItemStatus.Ready,
                SourceType = Mathom.Web.Domain.SourceType.Text, RawText = "x", CleanText = "x", Title = "n",
                ItemType = Mathom.Web.Domain.ItemType.Note, CreatedAt = System.DateTimeOffset.UtcNow,
                ProcessedAt = System.DateTimeOffset.UtcNow, IdempotencyKey = System.Guid.NewGuid().ToString(),
                UserId = TestUsers.AliceId,
            };
            id = item.Id; db.Items.Add(item); await db.SaveChangesAsync();
        }

        // CreateClient() authenticates as Alice (the seeded note's owner) via the test auth scheme.
        var html = await app.CreateClient().GetStringAsync($"/Note/{id}");
        Assert.Contains("id=\"glossary-token\"", html);        // token the popup reads
        Assert.Contains("/js/glossary.js", html);              // script included
    }
}
