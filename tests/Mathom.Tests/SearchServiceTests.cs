using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Search;
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
}
