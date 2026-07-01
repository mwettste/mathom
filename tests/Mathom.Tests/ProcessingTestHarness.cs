using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Embeddings;
using Mathom.Web.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mathom.Tests;

/// <summary>
/// Shared helper that inserts a Pending text item, builds an ItemProcessor with the given
/// embedding client (and the standard set of fakes), runs ProcessAsync, and returns the item id.
/// Mirror the dependency wiring from ItemProcessorTests.
/// </summary>
public static class ProcessingTestHarness
{
    public static async Task<Guid> CaptureAndProcessAsync(
        PostgresFixture fixture, string userId, string rawText, IEmbeddingClient embeddings)
    {
        var item = Item.CreatePending(SourceType.Text, rawText, Guid.NewGuid().ToString(), userId, DateTimeOffset.UtcNow);

        await using (var seed = fixture.NewDbContext())
        {
            seed.Items.Add(item);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.NewDbContext())
        {
            var processor = new ItemProcessor(
                db,
                new FakeLlmClient(),
                new FakeTranscriber(),
                new FakeImageReader(),
                new FakeMediaStore(),
                new Mathom.Web.Media.PhotoVariantService(db, new FakeMediaStore(), new Mathom.Web.Media.ImageVariantProcessor()),
                new Mathom.Web.Glossary.GlossaryService(db),
                new Mathom.Web.Languages.UserLanguageService(db),
                embeddings,
                NullLogger<ItemProcessor>.Instance);
            await processor.ProcessAsync(item.Id, CancellationToken.None);
        }

        return item.Id;
    }
}
