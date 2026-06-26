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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ItemProcessorTests(PostgresFixture fx)
{
    private const string Uid = "processor-tests-user";

    [Fact]
    public async Task ProcessAsync_FillsFieldsAndTags_SetsReady()
    {
        await fx.EnsureUserAsync(Uid, "processor@example.com");
        var item = Item.CreatePending(SourceType.Text, "idea: build a thing", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var fake = new FakeLlmClient
        {
            Respond = _ => new CleanupResult("Build a thing", "Build a thing.", ItemType.Idea, true,
                new[] { new CleanupTag("projects", TagKind.Topic), new CleanupTag("thing", TagKind.Project) })
        };

        await using (var db = fx.NewDbContext())
        {
            var processor = new ItemProcessor(db, fake, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
                new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance);
            await processor.ProcessAsync(item.Id, CancellationToken.None);
        }

        await using (var verify = fx.NewDbContext())
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
        await fx.EnsureUserAsync(Uid, "processor@example.com");
        var item = Item.CreatePending(SourceType.Text, "keep me", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        await using (var db = fx.NewDbContext())
        {
            var processor = new ItemProcessor(db, new FakeLlmClient { Throw = true }, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
                new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance);
            await processor.ProcessAsync(item.Id, CancellationToken.None);
        }

        await using (var verify = fx.NewDbContext())
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
        await fx.EnsureUserAsync(Uid, "processor@example.com");
        var a = Item.CreatePending(SourceType.Text, "first", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        var b = Item.CreatePending(SourceType.Text, "second", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.AddRange(a, b);
            await seed.SaveChangesAsync();
        }

        var fake = new FakeLlmClient
        {
            Respond = raw => new CleanupResult(raw, raw, ItemType.Note, false,
                new[] { new CleanupTag("shared", TagKind.Topic) })
        };

        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, fake, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
                new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance).ProcessAsync(a.Id, CancellationToken.None);
        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, fake, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
                new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance).ProcessAsync(b.Id, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
            Assert.Equal(1, await verify.Tags.CountAsync(t => t.Name == "shared"));
    }

    [Fact]
    public async Task ProcessAsync_Voice_TranscribesThenCleans()
    {
        await fx.EnsureUserAsync(Uid, "processor@example.com");
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
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        var transcriber = new FakeTranscriber { Respond = _ => "spoken note about tomatoes" };
        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Tomatoes", raw.Trim(), ItemType.Idea, true,
                new[] { new CleanupTag("garden", TagKind.Topic) })
        };

        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, llm, transcriber, new FakeImageReader(), media,
                new Mathom.Web.Media.PhotoVariantService(db, media, new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
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
        await fx.EnsureUserAsync(Uid, "processor@example.com");
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
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, new FakeLlmClient(), new FakeTranscriber { Throw = true }, new FakeImageReader(), media,
                new Mathom.Web.Media.PhotoVariantService(db, media, new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance).ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
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
        await fx.EnsureUserAsync(u, u + "@example.com");

        System.Guid itemId;
        await using (var seed = fx.NewDbContext())
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

        await using var db = fx.NewDbContext();
        var processor = new ItemProcessor(db, llm, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
            new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
            new Mathom.Web.Glossary.GlossaryService(db),
            new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance);

        await processor.ProcessAsync(itemId, System.Threading.CancellationToken.None);

        await using var verify = fx.NewDbContext();
        var item2 = await verify.Items.Include(i => i.ItemTags).ThenInclude(t => t.Tag)
            .FirstAsync(i => i.Id == itemId);
        Assert.Equal(ItemStatus.Ready, item2.Status); // did not crash/fail
        Assert.Equal(new[] { "new1", "old2" }, item2.ItemTags.Select(it => it.Tag.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Process_AppliesVariantCorrection_ToCleanOutput()
    {
        var u = "ip-variant-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        System.Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            var term = new Mathom.Web.Domain.GlossaryTerm { Id = System.Guid.NewGuid(), UserId = u, Term = "FireSkills", CreatedAt = System.DateTimeOffset.UtcNow };
            term.Variants.Add(new Mathom.Web.Domain.GlossaryVariant { Id = System.Guid.NewGuid(), GlossaryTermId = term.Id, Text = "Fairstills", CreatedAt = System.DateTimeOffset.UtcNow });
            seed.GlossaryTerms.Add(term);
            var item = Mathom.Web.Domain.Item.CreatePending(Mathom.Web.Domain.SourceType.Text, "Meeting Fairstills today", System.Guid.NewGuid().ToString(), u, System.DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        // The fake LLM echoes the transcript into title/body and emits a "Fairstills" tag.
        var llm = new FakeLlmClient
        {
            Respond = raw => new Mathom.Web.Processing.CleanupResult(
                raw, raw, Mathom.Web.Domain.ItemType.Note, false,
                new System.Collections.Generic.List<Mathom.Web.Processing.CleanupTag> { new("Fairstills", Mathom.Web.Domain.TagKind.Topic) }),
        };

        await using var db = fx.NewDbContext();
        var processor = new Mathom.Web.Processing.ItemProcessor(db, llm, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
            new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
            new Mathom.Web.Glossary.GlossaryService(db),
            new Mathom.Web.Languages.UserLanguageService(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Processing.ItemProcessor>.Instance);

        await processor.ProcessAsync(itemId, System.Threading.CancellationToken.None);

        await using var verify = fx.NewDbContext();
        var saved = await verify.Items.Include(i => i.ItemTags).ThenInclude(t => t.Tag).FirstAsync(i => i.Id == itemId);
        Assert.Contains("FireSkills", saved.CleanText);
        Assert.DoesNotContain("Fairstills", saved.CleanText);
        Assert.Contains("FireSkills", saved.Title);
        Assert.Contains(saved.ItemTags, it => it.Tag.Name == "FireSkills");
        Assert.DoesNotContain(saved.ItemTags, it => it.Tag.Name == "Fairstills");
    }

    [Fact]
    public async Task Process_PassesUsersGlossary_ToCleanup()
    {
        var u = "ip-gloss-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        // seed a glossary term + a pending text item for this user
        Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            seed.GlossaryTerms.Add(new Mathom.Web.Domain.GlossaryTerm
            { Id = Guid.NewGuid(), UserId = u, Term = "Obersaxen", CreatedAt = DateTimeOffset.UtcNow });
            var item = Mathom.Web.Domain.Item.CreatePending(Mathom.Web.Domain.SourceType.Text, "raw text", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlmClient();
        await using var db = fx.NewDbContext();
        var processor = new Mathom.Web.Processing.ItemProcessor(
            db, llm, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
            new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
            new Mathom.Web.Glossary.GlossaryService(db),
            new Mathom.Web.Languages.UserLanguageService(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Processing.ItemProcessor>.Instance);

        await processor.ProcessAsync(itemId, CancellationToken.None);

        Assert.Contains("Obersaxen", llm.LastGlossary);
    }

    [Fact]
    public async Task Process_IncludesTermDescription_InCleanupGlossary()
    {
        var u = "ip-desc-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        System.Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            var term = new Mathom.Web.Domain.GlossaryTerm
            {
                Id = System.Guid.NewGuid(), UserId = u, Term = "FireSkills",
                CreatedAt = System.DateTimeOffset.UtcNow, Description = "our internal time-tracking product",
            };
            term.Variants.Add(new Mathom.Web.Domain.GlossaryVariant { Id = System.Guid.NewGuid(), GlossaryTermId = term.Id, Text = "Fairstills", CreatedAt = System.DateTimeOffset.UtcNow });
            seed.GlossaryTerms.Add(term);
            var item = Mathom.Web.Domain.Item.CreatePending(Mathom.Web.Domain.SourceType.Text, "note about FireSkills", System.Guid.NewGuid().ToString(), u, System.DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlmClient { Respond = raw => new Mathom.Web.Processing.CleanupResult(raw, raw, Mathom.Web.Domain.ItemType.Note, false, new System.Collections.Generic.List<Mathom.Web.Processing.CleanupTag>()) };
        await using var db = fx.NewDbContext();
        var processor = new Mathom.Web.Processing.ItemProcessor(db, llm, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
            new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
            new Mathom.Web.Glossary.GlossaryService(db),
            new Mathom.Web.Languages.UserLanguageService(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Processing.ItemProcessor>.Instance);

        await processor.ProcessAsync(itemId, System.Threading.CancellationToken.None);

        Assert.Contains("FireSkills (also heard as: Fairstills) — our internal time-tracking product", llm.LastGlossary);
    }

    [Fact]
    public async Task ProcessAsync_Photo_ReadsThenCleans()
    {
        await fx.EnsureUserAsync(Uid, "processor@example.com");
        var media = new FakeMediaStore();
        var tinyJpeg = await MakeTinyJpegAsync();
        string k1, k2;
        using (var a = new MemoryStream(tinyJpeg)) k1 = await media.SaveAsync(a, ".jpg", CancellationToken.None);
        using (var b = new MemoryStream(tinyJpeg)) k2 = await media.SaveAsync(b, ".jpg", CancellationToken.None);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k1, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k2, Order = 1, CreatedAt = DateTimeOffset.UtcNow });
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        var reader = new FakeImageReader { Respond = _ => "whiteboard: ship the thing" };
        var llm = new FakeLlmClient { Respond = raw => new CleanupResult("Ship it", raw.Trim(), ItemType.Task, true,
            new[] { new CleanupTag("work", TagKind.Topic) }) };

        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, llm, new FakeTranscriber(), reader, media,
                new Mathom.Web.Media.PhotoVariantService(db, media, new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Ready, loaded.Status);
            Assert.Equal("whiteboard: ship the thing", loaded.RawText);
            Assert.Equal("whiteboard: ship the thing", loaded.CleanText);
            Assert.Equal(ItemType.Task, loaded.ItemType);
        }
        Assert.Equal(1, reader.Calls);
        Assert.Equal(2, reader.LastImageCount);
    }

    [Fact]
    public async Task ProcessAsync_Photo_GeneratesVariant_AndFeedsItToReader()
    {
        await fx.EnsureUserAsync(Uid, "processor@example.com");
        var media = new FakeMediaStore();
        byte[] originalBytes;
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(3000, 2000))
        {
            using var ms = new MemoryStream();
            await img.SaveAsync(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            originalBytes = ms.ToArray();
        }
        string originalKey;
        using (var s = new MemoryStream(originalBytes)) originalKey = await media.SaveAsync(s, ".png", CancellationToken.None);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        item.Photos.Add(new ItemPhoto { MediaPath = originalKey, Order = 0, CreatedAt = DateTimeOffset.UtcNow });

        var reader = new FakeImageReader();
        Guid itemId;
        await using (var db = fx.NewDbContext()) { db.Items.Add(item); await db.SaveChangesAsync(); itemId = item.Id; }

        await using (var db = fx.NewDbContext())
        {
            var variants = new Mathom.Web.Media.PhotoVariantService(db, media, new Mathom.Web.Media.ImageVariantProcessor());
            await new ItemProcessor(db, new FakeLlmClient(), new FakeTranscriber(), reader, media, variants,
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(itemId, CancellationToken.None);
        }

        await using (var db = fx.NewDbContext())
        {
            var photo = await db.ItemPhotos.IgnoreQueryFilters().FirstAsync(p => p.ItemId == itemId);
            Assert.False(string.IsNullOrEmpty(photo.DisplayPath));
            // The reader got the variant, not the original.
            using var variantStream = await media.OpenReadAsync(photo.DisplayPath!, CancellationToken.None);
            using var vms = new MemoryStream(); await variantStream.CopyToAsync(vms);
            Assert.Single(reader.LastImages);
            Assert.Equal(vms.ToArray(), reader.LastImages[0]);
            Assert.NotEqual(originalBytes.Length, reader.LastImages[0].Length);
        }
    }

    [Fact]
    public async Task ProcessAsync_Photo_PassesGlossaryToReader()
    {
        var u = "photo-glossary-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var media = new FakeMediaStore();
        var tinyJpeg = await MakeTinyJpegAsync();
        string k; using (var a = new MemoryStream(tinyJpeg)) k = await media.SaveAsync(a, ".jpg", CancellationToken.None);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        await using (var seed = fx.NewDbContext())
        {
            seed.Items.Add(item);
            seed.GlossaryTerms.Add(new GlossaryTerm { Id = Guid.NewGuid(), UserId = u, Term = "Obersaxen", CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        var reader = new FakeImageReader();
        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, new FakeLlmClient(), new FakeTranscriber(), reader, media,
                new Mathom.Web.Media.PhotoVariantService(db, media, new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(item.Id, CancellationToken.None);

        Assert.Contains("Obersaxen", reader.LastGlossary);
    }

    [Fact]
    public async Task ProcessAsync_UsesContextScopedGlossary()
    {
        var u = "processor-context-user";
        await fx.EnsureUserAsync(u, u + "@example.com");

        var ctxId = Guid.NewGuid();
        var item = Item.CreatePending(SourceType.Text, "meeting about acme", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
        item.ContextId = ctxId;
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Context { Id = ctxId, UserId = u, Name = "Biz", CreatedAt = DateTimeOffset.UtcNow });
            // Variant "acme" -> term "Acme Corp", defined ONLY in this context.
            var term = new GlossaryTerm { Id = Guid.NewGuid(), UserId = u, ContextId = ctxId, Term = "Acme Corp", CreatedAt = DateTimeOffset.UtcNow };
            term.Variants.Add(new GlossaryVariant { Id = Guid.NewGuid(), GlossaryTermId = term.Id, Text = "acme", CreatedAt = DateTimeOffset.UtcNow });
            seed.GlossaryTerms.Add(term);
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        // Cleanup echoes the raw text so the deterministic corrector can act on it.
        var fake = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Meeting", raw, ItemType.Note, false, System.Array.Empty<CleanupTag>())
        };

        await using (var db = fx.NewDbContext())
        {
            var processor = new ItemProcessor(db, fake, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
                new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance);
            await processor.ProcessAsync(item.Id, CancellationToken.None);
        }

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Contains("Acme Corp", loaded.CleanText); // variant corrected via the context glossary
        }
    }

    [Fact]
    public async Task ProcessAsync_Photo_WithCaptureNote_PassesNoteAndPrepends()
    {
        await fx.EnsureUserAsync(Uid, "processor@example.com");
        var media = new FakeMediaStore();
        var k1 = await media.SaveAsync(new MemoryStream(new byte[] { 1, 2, 3 }), ".jpg", default);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        item.CaptureNote = "kitchen renovation quote";
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k1, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        var reader = new FakeImageReader { Respond = _ => "line items: tiles, labor" };
        var llm = new FakeLlmClient();

        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, llm, new FakeTranscriber(), reader, media,
                new Mathom.Web.Glossary.GlossaryService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal("kitchen renovation quote", reader.LastContext);
            Assert.Equal("kitchen renovation quote\n\nline items: tiles, labor", loaded.RawText);
        }
    }
  
  
    [Fact]
    public async Task ProcessAsync_Photo_EmptyRead_SetsFailed()
    {
        var u = "photo-empty-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var media = new FakeMediaStore();
        var tinyJpeg = await MakeTinyJpegAsync();
        string k; using (var a = new MemoryStream(tinyJpeg)) k = await media.SaveAsync(a, ".jpg", CancellationToken.None);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        var reader = new FakeImageReader { Respond = _ => "   " };  // whitespace == nothing readable
        var llm = new FakeLlmClient();
        await using (var db = fx.NewDbContext())
            await new ItemProcessor(db, llm, new FakeTranscriber(), reader, media,
                new Mathom.Web.Media.PhotoVariantService(db, media, new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db), new Mathom.Web.Languages.UserLanguageService(db), NullLogger<ItemProcessor>.Instance)
                .ProcessAsync(item.Id, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal(ItemStatus.Failed, loaded.Status);
            Assert.Contains("No readable content", loaded.Error);
        }
        Assert.Equal(0, llm.Calls);  // cleanup never ran on empty input
    }

    /// <summary>Returns bytes of a valid 1x1 JPEG so ImageVariantProcessor can decode it.</summary>
    private static async Task<byte[]> MakeTinyJpegAsync()
    {
        using var img = new Image<Rgb24>(1, 1);
        using var ms = new MemoryStream();
        await img.SaveAsync(ms, new JpegEncoder());
        return ms.ToArray();
    }
}
