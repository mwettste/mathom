using System;
using System.Net;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class NotePageTests
{
    private readonly PostgresFixture _fx;
    public NotePageTests(PostgresFixture fx) => _fx = fx;

    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
        });

    [Fact]
    public async Task Note_RendersFullUntruncatedBody()
    {
        var longText = string.Join(" ", System.Linq.Enumerable.Repeat("the quick brown fox jumped over the lazy dog", 30))
            + " UNIQUE_TAIL_MARKER";
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Ready,
            SourceType = SourceType.Text,
            RawText = longText,
            CleanText = longText,
            Title = "A long note",
            ItemType = ItemType.Note,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString(),
        };
        await using (var seed = _fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        using var app = CreateApp();
        var resp = await app.CreateClient().GetAsync($"/Note/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("A long note", html);
        Assert.Contains("UNIQUE_TAIL_MARKER", html); // the end of a long body is present (not clamped)
    }

    [Fact]
    public async Task Note_UnknownId_Returns404()
    {
        using var app = CreateApp();
        var resp = await app.CreateClient().GetAsync($"/Note/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
