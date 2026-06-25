using System;
using System.Net;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class NotePageTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> CreateAppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

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
            UserId = TestUsers.AliceId,
        };
        using var app = await CreateAppAsync();
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        var resp = await app.CreateClient().GetAsync($"/Note/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("A long note", html);
        Assert.Contains("UNIQUE_TAIL_MARKER", html); // the end of a long body is present (not clamped)
    }

    [Fact]
    public async Task Note_UnknownId_Returns404()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().GetAsync($"/Note/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task NoteDetail_RendersLanguageToggle_WhenTranslationsExist()
    {
        using var app = await CreateAppAsync();

        var id = Guid.NewGuid();
        await using (var db = fx.NewDbContext())
        {
            var item = new Item
            {
                Id = id,
                Status = ItemStatus.Ready,
                SourceType = SourceType.Text,
                RawText = "x",
                CleanText = "Inhalt",
                Title = "Titel",
                SourceLanguage = "de-CH",
                ItemType = ItemType.Note,
                CreatedAt = DateTimeOffset.UtcNow,
                ProcessedAt = DateTimeOffset.UtcNow,
                IdempotencyKey = Guid.NewGuid().ToString(),
                UserId = TestUsers.AliceId,
            };
            item.Translations.Add(new ItemTranslation
            {
                Id = Guid.NewGuid(),
                ItemId = id,
                Locale = "en",
                Title = "Title",
                CleanText = "Content",
            });
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }

        var html = await app.CreateClient().GetStringAsync($"/Note/{id}");
        Assert.Contains("German (Switzerland)", html);   // toggle pill for source locale
        Assert.Contains("English", html);                // toggle pill for translation
        Assert.Contains("Content", html);                // translated body present in DOM
    }
}
