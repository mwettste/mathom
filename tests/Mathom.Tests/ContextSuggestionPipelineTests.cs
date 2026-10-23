using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Languages;
using Mathom.Web.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ContextSuggestionPipelineTests(PostgresFixture fx)
{
    private static ItemProcessor NewProcessor(Mathom.Web.Data.MathomDbContext db, ILlmClient llm)
        => new(db, llm, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
               new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
               new Mathom.Web.Glossary.GlossaryService(db), new UserLanguageService(db),
               new Mathom.Web.Contexts.ContextService(db),
               NullLogger<ItemProcessor>.Instance);

    // Seeds a user with contexts named "A".."" in alphabetical (= ListAsync) order.
    private async Task<(string Uid, List<Context> Ctxs, Item Item)> SeedAsync(
        int contextCount, Guid? itemContextId = null, Guid? suggested = null, bool contextOnItem = false)
    {
        var uid = "suggest-" + Guid.NewGuid().ToString("N");
        await fx.EnsureUserAsync(uid, uid + "@example.com");
        var ctxs = Enumerable.Range(0, contextCount)
            .Select(i => new Context { Id = Guid.NewGuid(), UserId = uid, Name = ((char)('A' + i)).ToString(), CreatedAt = DateTimeOffset.UtcNow })
            .ToList();
        var item = Item.CreatePending(SourceType.Text, "raw note", Guid.NewGuid().ToString(), uid, DateTimeOffset.UtcNow);
        if (contextOnItem) item.ContextId = ctxs[0].Id;
        item.SuggestedContextId = suggested;
        await using var seed = fx.NewDbContext();
        seed.Contexts.AddRange(ctxs);
        seed.Items.Add(item);
        await seed.SaveChangesAsync();
        return (uid, ctxs, item);
    }

    private async Task<Item> RunAsync(FakeLlmClient fake, Guid itemId)
    {
        await using (var db = fx.NewDbContext())
            await NewProcessor(db, fake).ProcessAsync(itemId, CancellationToken.None);
        await using var verify = fx.NewDbContext();
        return await verify.Items.AsNoTracking().SingleAsync(i => i.Id == itemId);
    }

    [Fact]
    public async Task InboxItem_WithContexts_StoresSuggestion()
    {
        var (_, ctxs, item) = await SeedAsync(3);
        var fake = new FakeLlmClient { SuggestRespond = (_, _, _) => "c2" };
        var loaded = await RunAsync(fake, item.Id);
        Assert.Equal(ItemStatus.Ready, loaded.Status);
        Assert.Equal(ctxs[1].Id, loaded.SuggestedContextId);
        Assert.Null(loaded.ContextId);
        Assert.Equal(new[] { "c1", "c2", "c3" }, fake.LastCandidates.Select(c => c.Key));
        Assert.Equal(new[] { "A", "B", "C" }, fake.LastCandidates.Select(c => c.Name));
    }

    [Fact]
    public async Task InboxItem_ModelSaysNone_NullSuggestion_Ready()
    {
        var (_, _, item) = await SeedAsync(2);
        var fake = new FakeLlmClient { SuggestRespond = (_, _, _) => null };
        var loaded = await RunAsync(fake, item.Id);
        Assert.Equal(ItemStatus.Ready, loaded.Status);
        Assert.Null(loaded.SuggestedContextId);
        Assert.Equal(1, fake.SuggestCalls);
    }

    [Fact]
    public async Task InboxItem_ModelReturnsUnknownKey_NullSuggestion_Ready()
    {
        var (_, _, item) = await SeedAsync(2);
        var fake = new FakeLlmClient { SuggestRespond = (_, _, _) => "c9" };
        var loaded = await RunAsync(fake, item.Id);
        Assert.Equal(ItemStatus.Ready, loaded.Status);
        Assert.Null(loaded.SuggestedContextId);
    }

    [Fact]
    public async Task SuggestionThrows_ItemStillReady_NullSuggestion()
    {
        var (_, _, item) = await SeedAsync(2);
        var fake = new FakeLlmClient { ThrowSuggest = true };
        var loaded = await RunAsync(fake, item.Id);
        Assert.Equal(ItemStatus.Ready, loaded.Status);
        Assert.Null(loaded.Error);
        Assert.Null(loaded.SuggestedContextId);
        Assert.Equal(1, fake.SuggestCalls);
    }

    [Fact]
    public async Task ItemInContext_NoSuggestCall_AndClearsStaleSuggestion()
    {
        var (uid, ctxs, item) = await SeedAsync(2, suggested: null, contextOnItem: true);
        await using (var db = fx.NewDbContext())
        {
            var row = await db.Items.SingleAsync(i => i.Id == item.Id);
            row.SuggestedContextId = ctxs[1].Id;
            await db.SaveChangesAsync();
        }
        var fake = new FakeLlmClient { SuggestRespond = (_, _, _) => "c1" };
        var loaded = await RunAsync(fake, item.Id);
        Assert.Equal(ItemStatus.Ready, loaded.Status);
        Assert.Equal(0, fake.SuggestCalls);
        Assert.Null(loaded.SuggestedContextId);
    }

    [Fact]
    public async Task UserWithoutContexts_NoSuggestCall()
    {
        var (_, _, item) = await SeedAsync(0);
        var fake = new FakeLlmClient { SuggestRespond = (_, _, _) => "c1" };
        var loaded = await RunAsync(fake, item.Id);
        Assert.Equal(ItemStatus.Ready, loaded.Status);
        Assert.Equal(0, fake.SuggestCalls);
        Assert.Null(loaded.SuggestedContextId);
    }

    [Fact]
    public async Task Candidates_UseRecentReadyTitlesOfOwnUserOnly()
    {
        var (uid, ctxs, item) = await SeedAsync(2);
        var other = "suggest-other-" + Guid.NewGuid().ToString("N");
        await fx.EnsureUserAsync(other, other + "@example.com");
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);
        Item Make(string userId, Guid? ctx, string? title, ItemStatus status, int minutes, bool deleted = false) => new()
        {
            Id = Guid.NewGuid(), Status = status, SourceType = SourceType.Text, RawText = "x",
            Title = title, CleanText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            UserId = userId, ContextId = ctx, CreatedAt = baseTime.AddMinutes(minutes),
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        var items = new List<Item>();
        for (var i = 1; i <= 12; i++) items.Add(Make(uid, ctxs[0].Id, $"T{i:00}", ItemStatus.Ready, i));
        items.Add(Make(uid, ctxs[0].Id, "PENDING", ItemStatus.Pending, 100));
        items.Add(Make(uid, ctxs[0].Id, "DELETED", ItemStatus.Ready, 101, deleted: true));
        items.Add(Make(uid, ctxs[0].Id, "", ItemStatus.Ready, 102));
        items.Add(Make(uid, ctxs[0].Id, null, ItemStatus.Ready, 103));
        items.Add(Make(uid, null, "INBOX", ItemStatus.Ready, 104));
        items.Add(Make(other, null, "OTHERUSER", ItemStatus.Ready, 105));
        items.Add(Make(uid, ctxs[1].Id, "B1", ItemStatus.Ready, 1));
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.AddRange(items);
            await seed.SaveChangesAsync();
        }

        var fake = new FakeLlmClient();
        await RunAsync(fake, item.Id);

        var a = fake.LastCandidates.Single(c => c.Name == "A");
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(i => $"T{i:00}"), a.RecentTitles);
        var b = fake.LastCandidates.Single(c => c.Name == "B");
        Assert.Equal(new[] { "B1" }, b.RecentTitles);
    }

    [Fact]
    public async Task Candidates_IncludeContextGlossaryTerms_Max30()
    {
        var (uid, ctxs, item) = await SeedAsync(2);
        await using (var seed = fx.NewDbContext())
        {
            for (var i = 0; i < 35; i++)
                seed.GlossaryTerms.Add(new GlossaryTerm
                {
                    Id = Guid.NewGuid(), UserId = uid, ContextId = ctxs[0].Id, Term = $"term{i:00}",
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i),
                });
            seed.GlossaryTerms.Add(new GlossaryTerm
            {
                Id = Guid.NewGuid(), UserId = uid, ContextId = ctxs[1].Id, Term = "onlyB", CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var fake = new FakeLlmClient();
        await RunAsync(fake, item.Id);

        var a = fake.LastCandidates.Single(c => c.Name == "A");
        Assert.Equal(30, a.GlossaryTerms.Count);
        Assert.Equal("term00", a.GlossaryTerms[0]);
        var b = fake.LastCandidates.Single(c => c.Name == "B");
        Assert.Equal(new[] { "onlyB" }, b.GlossaryTerms);
    }

    [Fact]
    public async Task Reprocess_InboxItem_RecomputesSuggestion()
    {
        var (_, ctxs, item) = await SeedAsync(2);
        var first = await RunAsync(new FakeLlmClient { SuggestRespond = (_, _, _) => "c1" }, item.Id);
        Assert.Equal(ctxs[0].Id, first.SuggestedContextId);

        await using (var db = fx.NewDbContext())
        {
            var row = await db.Items.SingleAsync(i => i.Id == item.Id);
            row.Status = ItemStatus.Pending;
            await db.SaveChangesAsync();
        }
        var second = await RunAsync(new FakeLlmClient { SuggestRespond = (_, _, _) => "c2" }, item.Id);
        Assert.Equal(ctxs[1].Id, second.SuggestedContextId);
    }
}
