using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Search;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class SearchServiceTests
{
    private const string Uid = "search-tests-user";

    private readonly PostgresFixture _fx;
    public SearchServiceTests(PostgresFixture fx) => _fx = fx;

    private static Item Ready(string title, string clean, ItemType type, DateTimeOffset created, bool actionable = false)
        => new()
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Ready,
            SourceType = SourceType.Text,
            RawText = clean,
            CleanText = clean,
            Title = title,
            ItemType = type,
            Actionable = actionable,
            CreatedAt = created,
            ProcessedAt = created,
            IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = Uid,
        };

    // Seeds a Ready item owned by userId with the given tags, get-or-creating
    // shared Tag rows so the unique (Name, Kind) index isn't violated across items.
    private async Task<Item> SeedTaggedAsync(
        string userId, string title, ItemType type, bool actionable, params string[] tags)
    {
        await _fx.EnsureUserAsync(userId, userId + "@example.com");
        await using var db = _fx.NewDbContext();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Ready,
            SourceType = SourceType.Text,
            RawText = title,
            CleanText = title,
            Title = title,
            ItemType = type,
            Actionable = actionable,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = userId,
        };
        foreach (var name in tags)
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name && t.Kind == TagKind.Topic)
                      ?? new Tag { Name = name, Kind = TagKind.Topic };
            item.ItemTags.Add(new ItemTag { Item = item, Tag = tag });
        }
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Timeline_ReturnsReadyNewestFirst()
    {
        await _fx.EnsureUserAsync(Uid, "search@example.com");
        var older = Ready("Older", "older body about gardening", ItemType.Note, DateTimeOffset.UtcNow.AddHours(-2));
        var newer = Ready("Newer", "newer body about coding", ItemType.Idea, DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext()) { seed.Items.AddRange(older, newer); await seed.SaveChangesAsync(); }

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db).TimelineAsync(Uid, 50, CancellationToken.None);

        var ids = result.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf(newer.Id) < ids.IndexOf(older.Id));
    }

    [Fact]
    public async Task Timeline_IncludesInFlightItems_WithStatus()
    {
        await _fx.EnsureUserAsync(Uid, "search@example.com");
        var pending = new Item
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Pending,
            SourceType = SourceType.Voice,
            RawText = "",
            IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UserId = Uid,
        };
        await using (var seed = _fx.NewDbContext()) { seed.Items.Add(pending); await seed.SaveChangesAsync(); }

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db).TimelineAsync(Uid, 50, CancellationToken.None);

        var found = result.Single(r => r.Id == pending.Id);
        Assert.Equal(ItemStatus.Pending, found.Status);
        Assert.Equal(SourceType.Voice, found.SourceType);
    }

    [Fact]
    public async Task Search_MatchesKeywordInBody()
    {
        await _fx.EnsureUserAsync(Uid, "search@example.com");
        var match = Ready("Recipe", "a note about sourdough bread", ItemType.Reference, DateTimeOffset.UtcNow);
        var noMatch = Ready("Other", "completely unrelated content", ItemType.Note, DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext()) { seed.Items.AddRange(match, noMatch); await seed.SaveChangesAsync(); }

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db).SearchAsync(Uid, "sourdough", new SearchFilters(null, null), 50, CancellationToken.None);

        Assert.Contains(result, r => r.Id == match.Id);
        Assert.DoesNotContain(result, r => r.Id == noMatch.Id);
    }

    [Fact]
    public async Task Search_AppliesTypeFilter()
    {
        await _fx.EnsureUserAsync(Uid, "search@example.com");
        var idea = Ready("Idea one", "shared keyword alpha", ItemType.Idea, DateTimeOffset.UtcNow);
        var note = Ready("Note one", "shared keyword alpha", ItemType.Note, DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext()) { seed.Items.AddRange(idea, note); await seed.SaveChangesAsync(); }

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db).SearchAsync(Uid, "alpha", new SearchFilters(ItemType.Idea, null), 50, CancellationToken.None);

        Assert.Contains(result, r => r.Id == idea.Id);
        Assert.DoesNotContain(result, r => r.Id == note.Id);
    }

    [Fact]
    public async Task Query_FiltersByTag_CaseInsensitive()
    {
        var u = "qtag-user";
        var match = await SeedTaggedAsync(u, "Has work tag", ItemType.Note, false, "Work");
        await SeedTaggedAsync(u, "Other", ItemType.Note, false, "home");

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db)
            .QueryAsync(u, null, new SearchFilters(Tag: "work"), 50, CancellationToken.None);

        Assert.Contains(result, r => r.Id == match.Id);
        Assert.Single(result);
    }

    [Fact]
    public async Task Query_TagFilter_IsUserScoped()
    {
        var a = "iso-user-a";
        var b = "iso-user-b";
        var aItem = await SeedTaggedAsync(a, "A work note", ItemType.Note, false, "work");
        await SeedTaggedAsync(b, "B work note", ItemType.Note, false, "work");

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db)
            .QueryAsync(a, null, new SearchFilters(Tag: "work"), 50, CancellationToken.None);

        Assert.All(result, r => Assert.Equal(aItem.Id, r.Id)); // only A's item
        Assert.Single(result);
    }

    [Fact]
    public async Task Query_CombinesTagAndType()
    {
        var u = "qcombine-user";
        var want = await SeedTaggedAsync(u, "work task", ItemType.Task, false, "work");
        await SeedTaggedAsync(u, "work note", ItemType.Note, false, "work");

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db)
            .QueryAsync(u, null, new SearchFilters(ItemType.Task, null, "work"), 50, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(want.Id, result[0].Id);
    }

    [Fact]
    public async Task Query_NoFilters_ReturnsNewestFirst()
    {
        var u = "qnone-user";
        await _fx.EnsureUserAsync(u, u + "@example.com");
        var older = Ready("Older", "older", ItemType.Note, DateTimeOffset.UtcNow.AddHours(-2));
        var newer = Ready("Newer", "newer", ItemType.Idea, DateTimeOffset.UtcNow);
        older.UserId = u; newer.UserId = u;
        await using (var seed = _fx.NewDbContext()) { seed.Items.AddRange(older, newer); await seed.SaveChangesAsync(); }

        await using var db = _fx.NewDbContext();
        var result = await new SearchService(db)
            .QueryAsync(u, null, new SearchFilters(), 50, CancellationToken.None);

        var ids = result.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf(newer.Id) < ids.IndexOf(older.Id));
    }
}
