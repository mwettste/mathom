using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Search;

public record TranslationSummary(string Locale, string Title, string CleanText);

public record ItemSummary(
    Guid Id,
    string? Title,
    string? CleanText,
    ItemType? ItemType,
    DateTimeOffset CreatedAt,
    ItemStatus Status,
    SourceType SourceType,
    bool Actionable,
    IReadOnlyList<string> Tags,
    string? SourceLanguage,
    IReadOnlyList<TranslationSummary> Translations);

public record SearchFilters(ItemType? ItemType = null, bool? Actionable = null, string? Tag = null);

public record ItemDetail(
    Guid Id,
    string? Title,
    string? CleanText,
    string RawText,
    ItemType? ItemType,
    SourceType SourceType,
    ItemStatus Status,
    bool Actionable,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    string? Error,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> PhotoExternalIds,
    string? SourceLanguage,
    IReadOnlyList<TranslationSummary> Translations);

public class SearchService(MathomDbContext db)
{
    // Full single item for the detail page (includes the raw transcript and any error).
    public async Task<ItemDetail?> GetAsync(string userId, Guid id, CancellationToken ct)
    {
        return await db.Items
            .Where(i => i.Id == id && i.UserId == userId)
            .Select(i => new ItemDetail(
                i.Id, i.Title, i.CleanText, i.RawText, i.ItemType, i.SourceType,
                i.Status, i.Actionable, i.CreatedAt, i.ProcessedAt, i.Error,
                i.ItemTags.Select(it => it.Tag.Name).ToList(),
                i.Photos.OrderBy(p => p.Order).Select(p => p.ExternalId).ToList(),
                i.SourceLanguage,
                i.Translations.Select(t => new TranslationSummary(t.Locale, t.Title, t.CleanText)).ToList()))
            .FirstOrDefaultAsync(ct);
    }

    // Timeline shows ALL recent items regardless of status, so freshly-captured items
    // appear immediately with their in-flight status (captured / transcribing / failed)
    // and settle into Ready as the background worker finishes.
    public async Task<IReadOnlyList<ItemSummary>> TimelineAsync(string userId, Guid? contextId, int take, CancellationToken ct)
    {
        var items = db.Items.Where(i => i.UserId == userId);
        items = contextId is { } cid ? items.Where(i => i.ContextId == cid) : items.Where(i => i.ContextId == null);
        return await items
            .OrderByDescending(i => i.CreatedAt)
            .Take(take)
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList(),
                i.SourceLanguage,
                i.Translations.Select(t => new TranslationSummary(t.Locale, t.Title, t.CleanText)).ToList()))
            .ToListAsync(ct);
    }

    // Search only returns finished items — in-flight items have no clean text to match.
    public Task<IReadOnlyList<ItemSummary>> SearchAsync(
        string userId, Guid? contextId, string query, SearchFilters filters, int take, CancellationToken ct)
        => QueryAsync(userId, contextId, query, filters, take, ct);

    // Unified, user-scoped query. With no text query and no filters it is the
    // timeline (all statuses, newest first). A text query restricts to Ready +
    // full-text match (rank order). Type/Actionable/Tag filters narrow further.
    public async Task<IReadOnlyList<ItemSummary>> QueryAsync(
        string userId, Guid? contextId, string? q, SearchFilters filters, int take, CancellationToken ct)
    {
        var items = db.Items.Where(i => i.UserId == userId);
        items = contextId is { } cid ? items.Where(i => i.ContextId == cid) : items.Where(i => i.ContextId == null);

        var hasQuery = !string.IsNullOrWhiteSpace(q);
        if (hasQuery)
        {
            var query = q!;
            items = items
                .Where(i => i.Status == ItemStatus.Ready)
                .Where(i => i.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", query))
                         || i.Translations.Any(t => t.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", query))));
        }

        if (filters.ItemType is { } t) items = items.Where(i => i.ItemType == t);
        if (filters.Actionable is { } a) items = items.Where(i => i.Actionable == a);
        if (!string.IsNullOrWhiteSpace(filters.Tag))
        {
            var tag = filters.Tag!.ToLower();
            items = items.Where(i => i.ItemTags.Any(it => it.Tag.Name.ToLower() == tag));
        }

        items = hasQuery
            ? items.OrderByDescending(i => i.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("simple", q!)))
            : items.OrderByDescending(i => i.CreatedAt);

        return await items
            .Take(take)
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList(),
                i.SourceLanguage,
                i.Translations.Select(t => new TranslationSummary(t.Locale, t.Title, t.CleanText)).ToList()))
            .ToListAsync(ct);
    }
}
