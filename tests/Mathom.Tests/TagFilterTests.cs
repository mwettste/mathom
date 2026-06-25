using System.Net;
using System.Threading.Tasks;
using Mathom.Web.Capture;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class TagFilterTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    // Seeds a Ready item (as Alice) by capturing then promoting it to Ready with a tag,
    // using a direct DbContext so the integration test has deterministic data.
    private async Task SeedReadyAsync(string title, string tag)
    {
        await using var db = fx.NewDbContext();
        var item = new Mathom.Web.Domain.Item
        {
            Id = System.Guid.NewGuid(),
            Status = Mathom.Web.Domain.ItemStatus.Ready,
            SourceType = Mathom.Web.Domain.SourceType.Text,
            RawText = title, CleanText = title, Title = title,
            ItemType = Mathom.Web.Domain.ItemType.Note,
            CreatedAt = System.DateTimeOffset.UtcNow, ProcessedAt = System.DateTimeOffset.UtcNow,
            IdempotencyKey = System.Guid.NewGuid().ToString(),
            UserId = TestUsers.AliceId,
        };
        var t = await db.Tags.FirstOrDefaultAsync(x => x.Name == tag && x.Kind == Mathom.Web.Domain.TagKind.Topic)
                 ?? new Mathom.Web.Domain.Tag { Name = tag, Kind = Mathom.Web.Domain.TagKind.Topic };
        item.ItemTags.Add(new Mathom.Web.Domain.ItemTag { Item = item, Tag = t });
        db.Items.Add(item);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TagFilter_ShowsOnlyMatchingNotes()
    {
        using var app = await AppAsync();
        await SeedReadyAsync("alpha-note", "alpha");
        await SeedReadyAsync("beta-note", "beta");

        var html = await app.CreateClient().GetStringAsync("/?tag=alpha");

        Assert.Contains("alpha-note", html);
        Assert.DoesNotContain("beta-note", html);
    }

    [Fact]
    public async Task TypeFilter_Renders()
    {
        using var app = await AppAsync();
        var resp = await app.CreateClient().GetAsync("/?type=task");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task EntryTags_AreClickableLinks()
    {
        using var app = await AppAsync();
        await SeedReadyAsync("tagged-note", "alpha");

        var html = await app.CreateClient().GetStringAsync("/");

        // The tag renders as a link to its filtered view, not a plain span.
        Assert.Contains("href=\"/?tag=alpha\"", html);
    }

    [Fact]
    public async Task ActiveFilter_RendersRemovableChip()
    {
        using var app = await AppAsync();
        await SeedReadyAsync("tagged-note", "alpha");

        var html = await app.CreateClient().GetStringAsync("/?tag=alpha");

        Assert.Contains("class=\"chip\"", html);   // active-filter chip present
        Assert.Contains("href=\"/\"", html);        // chip removal / clear returns to /
    }

    [Fact]
    public async Task FilteredNoMatch_ShowsFilteredEmptyState()
    {
        using var app = await AppAsync();
        await SeedReadyAsync("tagged-note", "alpha");

        var html = await app.CreateClient().GetStringAsync("/?tag=doesnotexist");

        Assert.Contains("No notes match", html);
    }
}
