using System;
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
public class MultiLanguagePipelineTests(PostgresFixture fx)
{
    private static ItemProcessor NewProcessor(Mathom.Web.Data.MathomDbContext db, ILlmClient llm)
        => new(db, llm, new FakeTranscriber(), new FakeImageReader(), new FakeMediaStore(),
               new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
               new Mathom.Web.Glossary.GlossaryService(db), new UserLanguageService(db),
               new FakeEmbeddingClient(),
               NullLogger<ItemProcessor>.Instance);

    [Fact]
    public async Task Process_DetectsSource_AndTranslatesIntoOtherActiveLanguages()
    {
        var u = "ml-translate-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            var svc = new UserLanguageService(seed);
            await svc.AddAsync(u, "de-CH", CancellationToken.None);  // primary
            await svc.AddAsync(u, "en", CancellationToken.None);
            var item = Item.CreatePending(SourceType.Text, "Hallo Welt", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Hallo", raw, ItemType.Note, false,
                new[] { new CleanupTag("greeting", TagKind.Topic) }, Language: "de"),
            TranslateRespond = (title, text, locale) => new TranslationResult("Hello", "Hello world"),
        };

        await using (var db = fx.NewDbContext())
            await NewProcessor(db, llm).ProcessAsync(itemId, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.Include(i => i.Translations).SingleAsync(i => i.Id == itemId);
            Assert.Equal(ItemStatus.Ready, loaded.Status);
            Assert.Equal("de-CH", loaded.SourceLanguage);   // resolved to primary German variant
            Assert.Equal("Hallo", loaded.Title);            // source kept on Item
            var en = Assert.Single(loaded.Translations);
            Assert.Equal("en", en.Locale);
            Assert.Equal("Hello", en.Title);
        }
    }

    [Fact]
    public async Task Process_TranslationFailure_LeavesNoteReady_WithLocaleMissing()
    {
        var u = "ml-failtrans-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            var svc = new UserLanguageService(seed);
            await svc.AddAsync(u, "en", CancellationToken.None);
            await svc.AddAsync(u, "de-DE", CancellationToken.None);
            var item = Item.CreatePending(SourceType.Text, "hello", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Hello", raw, ItemType.Note, false,
                Array.Empty<CleanupTag>(), Language: "en"),
            ThrowTranslate = true,   // every translation fails
        };

        await using (var db = fx.NewDbContext())
            await NewProcessor(db, llm).ProcessAsync(itemId, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.Include(i => i.Translations).SingleAsync(i => i.Id == itemId);
            Assert.Equal(ItemStatus.Ready, loaded.Status);   // best-effort: still Ready
            Assert.Equal("en", loaded.SourceLanguage);
            Assert.Empty(loaded.Translations);               // de-DE missing, re-processable
        }
    }

    [Fact]
    public async Task Process_NoConfiguredLanguages_ProducesNoTranslations_ButSetsSource()
    {
        var u = "ml-nolang-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            var item = Item.CreatePending(SourceType.Text, "Bonjour", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }
        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Bonjour", raw, ItemType.Note, false, Array.Empty<CleanupTag>(), Language: "fr"),
        };

        await using (var db = fx.NewDbContext())
            await NewProcessor(db, llm).ProcessAsync(itemId, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.Include(i => i.Translations).SingleAsync(i => i.Id == itemId);
            Assert.Equal("fr-FR", loaded.SourceLanguage);   // catalog default for detected base
            Assert.Empty(loaded.Translations);
            Assert.Equal(0, llm.TranslateCalls);
        }
    }

    [Fact]
    public async Task Reprocess_RefreshesTranslations()
    {
        var u = "ml-reproc-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        Guid itemId;
        await using (var seed = fx.NewDbContext())
        {
            var svc = new UserLanguageService(seed);
            await svc.AddAsync(u, "en", CancellationToken.None);
            await svc.AddAsync(u, "de-DE", CancellationToken.None);
            var item = Item.CreatePending(SourceType.Text, "hello", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
            itemId = item.Id;
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }
        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Hello", raw, ItemType.Note, false, Array.Empty<CleanupTag>(), Language: "en"),
            TranslateRespond = (t, x, locale) => new TranslationResult("Hallo", "Hallo"),
        };

        await using (var db = fx.NewDbContext())
            await NewProcessor(db, llm).ProcessAsync(itemId, CancellationToken.None);
        // process again (simulates Re-process) — must not duplicate translation rows
        await using (var db = fx.NewDbContext())
            await NewProcessor(db, llm).ProcessAsync(itemId, CancellationToken.None);

        await using (var verify = fx.NewDbContext())
        {
            var loaded = await verify.Items.Include(i => i.Translations).SingleAsync(i => i.Id == itemId);
            Assert.Single(loaded.Translations);   // exactly one de-DE row, not two
        }
    }
}
