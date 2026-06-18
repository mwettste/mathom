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
            var processor = new ItemProcessor(db, fake, new FakeTranscriber(), new FakeMediaStore(), NullLogger<ItemProcessor>.Instance);
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
            var processor = new ItemProcessor(db, new FakeLlmClient { Throw = true }, new FakeTranscriber(), new FakeMediaStore(), NullLogger<ItemProcessor>.Instance);
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
            await new ItemProcessor(db, fake, new FakeTranscriber(), new FakeMediaStore(), NullLogger<ItemProcessor>.Instance).ProcessAsync(a.Id, CancellationToken.None);
        await using (var db = _fx.NewDbContext())
            await new ItemProcessor(db, fake, new FakeTranscriber(), new FakeMediaStore(), NullLogger<ItemProcessor>.Instance).ProcessAsync(b.Id, CancellationToken.None);

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
            await new ItemProcessor(db, llm, transcriber, media, NullLogger<ItemProcessor>.Instance)
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
                NullLogger<ItemProcessor>.Instance).ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = _fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Failed, loaded.Status);
            Assert.False(string.IsNullOrEmpty(loaded.Error));
        }
    }
}
