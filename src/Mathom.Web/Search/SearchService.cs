using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Search;

public record ItemSummary(
    Guid Id,
    string? Title,
    string? CleanText,
    ItemType? ItemType,
    DateTimeOffset CreatedAt,
    ItemStatus Status,
    SourceType SourceType,
    bool Actionable,
    IReadOnlyList<string> Tags);

public record SearchFilters(ItemType? ItemType, bool? Actionable);

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
    IReadOnlyList<string> Tags);

public class SearchService
{
    private readonly MathomDbContext _db;
    public SearchService(MathomDbContext db) => _db = db;

    // Full single item for the detail page (includes the raw transcript and any error).
    public async Task<ItemDetail?> GetAsync(string userId, Guid id, CancellationToken ct)
    {
        return await _db.Items
            .Where(i => i.Id == id && i.UserId == userId)
            .Select(i => new ItemDetail(
                i.Id, i.Title, i.CleanText, i.RawText, i.ItemType, i.SourceType,
                i.Status, i.Actionable, i.CreatedAt, i.ProcessedAt, i.Error,
                i.ItemTags.Select(it => it.Tag.Name).ToList()))
            .FirstOrDefaultAsync(ct);
    }

    // Timeline shows ALL recent items regardless of status, so freshly-captured items
    // appear immediately with their in-flight status (captured / transcribing / failed)
    // and settle into Ready as the background worker finishes.
    public async Task<IReadOnlyList<ItemSummary>> TimelineAsync(string userId, int take, CancellationToken ct)
    {
        return await _db.Items
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(take)
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList()))
            .ToListAsync(ct);
    }

    // Search only returns finished items — in-flight items have no clean text to match.
    public async Task<IReadOnlyList<ItemSummary>> SearchAsync(
        string userId, string query, SearchFilters filters, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await TimelineAsync(userId, take, ct);

        var q = _db.Items
            .Where(i => i.UserId == userId)
            .Where(i => i.Status == ItemStatus.Ready)
            .Where(i => i.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", query)));

        if (filters.ItemType is { } t) q = q.Where(i => i.ItemType == t);
        if (filters.Actionable is { } a) q = q.Where(i => i.Actionable == a);

        return await q
            .OrderByDescending(i => i.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
            .Take(take)
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList()))
            .ToListAsync(ct);
    }
}
