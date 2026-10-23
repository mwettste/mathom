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
    Mathom.Web.Contexts.ContextService contexts,
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
                    var read = await imageReader.ExtractAsync(images, terms, item.CaptureNote, ct);
                    if (string.IsNullOrWhiteSpace(read))
                        throw new InvalidOperationException("No readable content found in the photo(s).");
                    item.RawText = string.IsNullOrWhiteSpace(item.CaptureNote)
                        ? read
                        : item.CaptureNote.Trim() + "\n\n" + read;
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

            // Best-effort: only Inbox items get a suggestion; any failure leaves it null.
            item.SuggestedContextId = null;
            if (item.ContextId is null)
            {
                try
                {
                    item.SuggestedContextId = await SuggestContextAsync(item, result, ct);
                }
                catch (Exception sex)
                {
                    logger.LogWarning(sex, "Context suggestion failed for item {ItemId}", item.Id);
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

            if (await WasRequeuedAsync(itemId, ct)) return;

            item.ProcessedAt = DateTimeOffset.UtcNow;
            item.Status = ItemStatus.Ready;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing failed for item {ItemId}", itemId);
            if (await WasRequeuedAsync(itemId, ct)) return;

            item.Status = ItemStatus.Failed;
            item.Error = ex.Message;
            await db.SaveChangesAsync(ct);
        }
    }

    private const int MaxSuggestTerms = 30;
    private const int MaxSuggestTitles = 10;

    // Asks the model which of the user's contexts fits an Inbox item. Never moves anything; returns
    // null when there are no contexts, the model says none, or it answers with an unknown key.
    private async Task<Guid?> SuggestContextAsync(Item item, CleanupResult result, CancellationToken ct)
    {
        var list = await contexts.ListAsync(item.UserId, ct);
        if (list.Count == 0) return null;

        var keyToId = new Dictionary<string, Guid>();
        var candidates = new List<ContextCandidate>();
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var key = "c" + (i + 1);
            keyToId[key] = c.Id;

            var terms = (await glossary.GetEntriesAsync(item.UserId, c.Id, ct))
                .Select(e => e.Term).Take(MaxSuggestTerms).ToList();
            var titles = await db.Items.AsNoTracking()
                .Where(x => x.UserId == item.UserId && x.ContextId == c.Id && x.Status == ItemStatus.Ready
                            && x.Id != item.Id && x.Title != null && x.Title != "")
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.Title!)
                .Take(MaxSuggestTitles)
                .ToListAsync(ct);
            candidates.Add(new ContextCandidate(key, c.Name, terms, titles));
        }

        var chosen = await llm.SuggestContextAsync(result.Title, result.CleanText, candidates, ct);
        return chosen is not null && keyToId.TryGetValue(chosen, out var id) ? id : null;
    }

    // Someone else (e.g. a move) re-queued the item while this run held a stale copy. Writing our
    // result would clobber that Pending status and lose the re-clean, so drop our changes instead.
    private async Task<bool> WasRequeuedAsync(Guid itemId, CancellationToken ct)
    {
        var current = await db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => i.Status)
            .FirstAsync(ct);
        if (current == ItemStatus.Processing) return false;
        logger.LogInformation("Item {ItemId} changed to {Status} during processing; abandoning this run.", itemId, current);
        db.ChangeTracker.Clear();
        return true;
    }
}
