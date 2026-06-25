using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

public class ItemProcessor(
    MathomDbContext db,
    ILlmClient llm,
    ITranscriber transcriber,
    IImageReader imageReader,
    IMediaStore media,
    Mathom.Web.Media.PhotoVariantService variants,
    Mathom.Web.Glossary.GlossaryService glossary,
    Mathom.Web.Languages.UserLanguageService userLanguages,
    ILogger<ItemProcessor> logger)
{
    public async Task ProcessAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.Items
            .Include(i => i.ItemTags)
            .Include(i => i.Photos)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;
        if (item.Status is not (ItemStatus.Pending or ItemStatus.Failed or ItemStatus.Processing)) return;

        item.Status = ItemStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            var entries = await glossary.GetEntriesAsync(item.UserId, item.ContextId, ct);
            if (entries.Count > 100)
            {
                logger.LogInformation("Glossary for user {User} has {Count} entries; injecting first 100.", item.UserId, entries.Count);
                entries = entries.Take(100).ToList();
            }
            var terms = entries.Select(e => e.Term).ToList();                       // Whisper bias
            var cleanupGlossary = entries
                .Select(e =>
                {
                    var s = e.Variants.Count > 0
                        ? $"{e.Term} (also heard as: {string.Join(", ", e.Variants)})"
                        : e.Term;
                    if (!string.IsNullOrWhiteSpace(e.Description))
                        s += $" — {e.Description}";
                    return s;
                })
                .ToList();                                                          // LLM prompt
            var variantToTerm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
                foreach (var v in e.Variants)
                    variantToTerm[v] = e.Term;                                       // deterministic corrector

            // Voice items have no text yet — transcribe the stored audio first.
            if (item.SourceType == SourceType.Voice && string.IsNullOrEmpty(item.RawText) && item.MediaPath is not null)
            {
                await using var audio = await media.OpenReadAsync(item.MediaPath, ct);
                item.RawText = await transcriber.TranscribeAsync(audio, item.MediaPath, terms, ct);
                await db.SaveChangesAsync(ct);
            }

            // Photo items have no text yet — read the stored image(s) with the vision model.
            if (item.SourceType == SourceType.Photo && string.IsNullOrEmpty(item.RawText) && item.Photos.Count > 0)
            {
                var streams = new List<Stream>();
                try
                {
                    var images = new List<ImageData>();
                    foreach (var p in item.Photos.OrderBy(p => p.Order))
                    {
                        var displayKey = await variants.EnsureDisplayAsync(p, ct);
                        var s = await media.OpenReadAsync(displayKey, ct);
                        streams.Add(s);
                        images.Add(new ImageData(s, displayKey));
                    }
                    var read = await imageReader.ExtractAsync(images, terms, ct);
                    if (string.IsNullOrWhiteSpace(read))
                        throw new InvalidOperationException("No readable content found in the photo(s).");
                    item.RawText = read;
                    await db.SaveChangesAsync(ct);
                }
                finally
                {
                    foreach (var s in streams) await s.DisposeAsync();
                }
            }

            var result = await llm.CleanupAsync(item.RawText, cleanupGlossary, ct);
            result = GlossaryCorrector.Apply(result, variantToTerm);

            var primaryLocale = await userLanguages.GetPrimaryLocaleAsync(item.UserId, ct);
            var activeLocales = await userLanguages.GetActiveLocalesAsync(item.UserId, ct);
            var sourceLocale = Mathom.Web.Languages.Locales.ResolveSourceLocale(result.Language, primaryLocale, activeLocales);

            item.SourceLanguage = sourceLocale;
            item.Title = result.Title;
            item.CleanText = result.CleanText;
            item.ItemType = result.ItemType;
            item.Actionable = result.Actionable;
            item.Error = null;

            // Re-translate from scratch so re-processing stays clean and picks up current languages.
            var staleTranslations = await db.ItemTranslations.Where(t => t.ItemId == item.Id).ToListAsync(ct);
            db.ItemTranslations.RemoveRange(staleTranslations);

            foreach (var locale in activeLocales.Where(l => l != sourceLocale))
            {
                try
                {
                    var tr = await llm.TranslateAsync(
                        result.Title, result.CleanText, locale,
                        Mathom.Web.Languages.Locales.StyleHint(locale), terms, ct);
                    db.ItemTranslations.Add(new ItemTranslation
                    {
                        Id = Guid.NewGuid(),
                        ItemId = item.Id,
                        Locale = locale,
                        Title = tr.Title,
                        CleanText = tr.CleanText,
                    });
                }
                catch (Exception tex)
                {
                    // Best-effort: a failed language is simply absent; the note still goes Ready.
                    logger.LogWarning(tex, "Translation to {Locale} failed for item {ItemId}", locale, item.Id);
                }
            }

            var desiredTagIds = new List<int>();
            foreach (var t in result.Tags)
            {
                var tag = await db.Tags.FirstOrDefaultAsync(x => x.Name == t.Name && x.Kind == t.Kind, ct);
                if (tag is null)
                {
                    tag = new Tag { Name = t.Name, Kind = t.Kind };
                    db.Tags.Add(tag);
                    await db.SaveChangesAsync(ct);
                }
                desiredTagIds.Add(tag.Id);
                if (!item.ItemTags.Any(it => it.TagId == tag.Id))
                    item.ItemTags.Add(new ItemTag { ItemId = item.Id, TagId = tag.Id });
            }
            // Reconcile: drop tags no longer in the cleanup result (keeps re-processing clean).
            item.ItemTags.RemoveAll(it => !desiredTagIds.Contains(it.TagId));

            item.ProcessedAt = DateTimeOffset.UtcNow;
            item.Status = ItemStatus.Ready;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing failed for item {ItemId}", itemId);
            item.Status = ItemStatus.Failed;
            item.Error = ex.Message;
            await db.SaveChangesAsync(ct);
        }
    }
}
