using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Mathom.Web.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ItemProcessorTests
{
    private const string Uid = "processor-tests-user";

    private readonly PostgresFixture _fx;
    public ItemProcessorTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ProcessAsync_FillsFieldsAndTags_SetsReady()
    {
        await _fx.EnsureUserAsync(Uid, "processor@example.com");
        var item = Item.CreatePending(SourceType.Text, "idea: build a thing", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext())
        {
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var fake = new FakeLlmClient
        {
            Respond = _ => new CleanupResult("Build a thing", "Build a thing.", ItemType.Idea, true,
                new[] { new CleanupTag("projects", TagKind.Topic), new CleanupTag("thing", TagKind.Project) })
        };

        await using (var db = _fx.NewDbContext())
        {
            var processor = new ItemProcessor(db, fake, new FakeTranscriber(), new FakeMediaStore(),
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance);
            await processor.ProcessAsync(item.Id, CancellationToken.None);
        }

        await using (var verify = _fx.NewDbContext())
        {
            var loaded = await verify.Items.Include(i => i.ItemTags).ThenInclude(it => it.Tag)
                .SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Ready, loaded.Status);
            Assert.Equal("Build a thing", loaded.Title);
            Assert.Equal(ItemType.Idea, loaded.ItemType);
            Assert.True(loaded.Actionable);
            Assert.NotNull(loaded.ProcessedAt);
            Assert.Equal(2, loaded.ItemTags.Count);
        }
    }

    [Fact]
    public async Task ProcessAsync_OnLlmFailure_SetsFailedAndKeepsRawText()
    {
        await _fx.EnsureUserAsync(Uid, "processor@example.com");
        var item = Item.CreatePending(SourceType.Text, "keep me", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext())
        {
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        await using (var db = _fx.NewDbContext())
        {
            var processor = new ItemProcessor(db, new FakeLlmClient { Throw = true }, new FakeTranscriber(), new FakeMediaStore(),
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance);
            await processor.ProcessAsync(item.Id, CancellationToken.None);
        }

        await using (var verify = _fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Failed, loaded.Status);
            Assert.Equal("keep me", loaded.RawText);
            Assert.False(string.IsNullOrEmpty(loaded.Error));
        }
    }

    [Fact]
    public async Task ProcessAsync_ReusesExistingTag()
    {
        await _fx.EnsureUserAsync(Uid, "processor@example.com");
        var a = Item.CreatePending(SourceType.Text, "first", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        var b = Item.CreatePending(SourceType.Text, "second", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = _fx.NewDbContext())
        {
            seed.Items.AddRange(a, b);
            await seed.SaveChangesAsync();
        }

        var fake = new FakeLlmClient
        {
            Respond = raw => new CleanupResult(raw, raw, ItemType.Note, false,
                new[] { new CleanupTag("shared", TagKind.Topic) })
        };

        await using (var db = _fx.NewDbContext())
            await new ItemProcessor(db, fake, new FakeTranscriber(), new FakeMediaStore(),
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance).ProcessAsync(a.Id, CancellationToken.None);
        await using (var db = _fx.NewDbContext())
            await new ItemProcessor(db, fake, new FakeTranscriber(), new FakeMediaStore(),
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance).ProcessAsync(b.Id, CancellationToken.None);

        await using (var verify = _fx.NewDbContext())
            Assert.Equal(1, await verify.Tags.CountAsync(t => t.Name == "shared"));
    }

    [Fact]
    public async Task ProcessAsync_Voice_TranscribesThenCleans()
    {
        await _fx.EnsureUserAsync(Uid, "processor@example.com");
        var media = new FakeMediaStore();
        string mediaKey;
        using (var audio = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
            mediaKey = await media.SaveAsync(audio, ".webm", CancellationToken.None);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Pending,
            SourceType = SourceType.Voice,
            RawText = "",
            MediaPath = mediaKey,
            IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UserId = Uid,
        };
        await using (var seed = _fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        var transcriber = new FakeTranscriber { Respond = _ => "spoken note about tomatoes" };
        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Tomatoes", raw.Trim(), ItemType.Idea, true,
                new[] { new CleanupTag("garden", TagKind.Topic) })
        };

        await using (var db = _fx.NewDbContext())
            await new ItemProcessor(db, llm, transcriber, media,
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = _fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Ready, loaded.Status);
            Assert.Equal("spoken note about tomatoes", loaded.RawText);   // transcript preserved
            Assert.Equal("spoken note about tomatoes", loaded.CleanText); // cleaned from transcript
            Assert.Equal(ItemType.Idea, loaded.ItemType);
        }
        Assert.Equal(1, transcriber.Calls);
    }

    [Fact]
    public async Task ProcessAsync_Voice_OnTranscriptionFailure_SetsFailed()
    {
        await _fx.EnsureUserAsync(Uid, "processor@example.com");
        var media = new FakeMediaStore();
        string mediaKey;
        using (var audio = new MemoryStream(new byte[] { 9 }))
            mediaKey = await media.SaveAsync(audio, ".webm", CancellationToken.None);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Pending,
            SourceType = SourceType.Voice,
            RawText = "",
            MediaPath = mediaKey,
            IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UserId = Uid,
        };
        await using (var seed = _fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        await using (var db = _fx.NewDbContext())
            await new ItemProcessor(db, new FakeLlmClient(), new FakeTranscriber { Throw = true }, media,
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance).ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = _fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Failed, loaded.Status);
            Assert.False(string.IsNullOrEmpty(loaded.Error));
        }
    }

    [Fact]
    public async Task Reprocess_ReconcilesTags_NoDuplicateNoStale()
    {
        var u = "reprocess-tags-user";
        await _fx.EnsureUserAsync(u, u + "@example.com");

        System.Guid itemId;
        await using (var seed = _fx.NewDbContext())
        {
            var item = new Item
            {
                Id = System.Guid.NewGuid(), Status = ItemStatus.Pending, SourceType = SourceType.Text,
                RawText = "raw", CleanText = "raw", Title = "t", ItemType = ItemType.Note,
                CreatedAt = System.DateTimeOffset.UtcNow, ProcessedAt = System.DateTimeOffset.UtcNow,
                IdempotencyKey = System.Guid.NewGuid().ToString(), UserId = u,
            };
            item.ItemTags.Add(new ItemTag { Item = item, Tag = new Tag { Name = "old1", Kind = TagKind.Topic } });
            item.ItemTags.Add(new ItemTag { Item = item, Tag = new Tag { Name = "old2", Kind = TagKind.Topic } });
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        // The LLM now returns old2 (kept) + new1 (added); old1 must be removed; no duplicate-key crash on old2.
        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("T", "clean", ItemType.Note, false,
                new System.Collections.Generic.List<CleanupTag> { new("old2", TagKind.Topic), new("new1", TagKind.Topic) }),
        };

        await using var db = _fx.NewDbContext();
        var processor = new ItemProcessor(db, llm, new FakeTranscriber(), new FakeMediaStore(),
            new Mathom.Web.Glossary.GlossaryService(db),
            NullLogger<ItemProcessor>.Instance);

        await processor.ProcessAsync(itemId, System.Threading.CancellationToken.None);

        await using var verify = _fx.NewDbContext();
        var item2 = await verify.Items.Include(i => i.ItemTags).ThenInclude(t => t.Tag)
            .FirstAsync(i => i.Id == itemId);
        Assert.Equal(ItemStatus.Ready, item2.Status); // did not crash/fail
        Assert.Equal(new[] { "new1", "old2" }, item2.ItemTags.Select(it => it.Tag.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Process_PassesUsersGlossary_ToCleanup()
    {
        var u = "ip-gloss-user";
        await _fx.EnsureUserAsync(u, u + "@example.com");
        // seed a glossary term + a pending text item for this user
        Guid itemId;
        await using (var seed = _fx.NewDbContext())
        {
            seed.GlossaryTerms.Add(new Mathom.Web.Domain.GlossaryTerm
            { Id = Guid.NewGuid(), UserId = u, Term = "Obersaxen", CreatedAt = DateTimeOffset.UtcNow });
            var item = Mathom.Web.Domain.Item.CreatePending(Mathom.Web.Domain.SourceType.Text, "raw text", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlmClient();
        await using var db = _fx.NewDbContext();
        var processor = new Mathom.Web.Processing.ItemProcessor(
            db, llm, new FakeTranscriber(), new FakeMediaStore(),
            new Mathom.Web.Glossary.GlossaryService(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Processing.ItemProcessor>.Instance);

        await processor.ProcessAsync(itemId, CancellationToken.None);

        Assert.Contains("Obersaxen", llm.LastGlossary);
    }
}
