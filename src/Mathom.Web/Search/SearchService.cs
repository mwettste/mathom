using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Search;

public record ItemSummary(Guid Id, string? Title, string? CleanText, ItemType? ItemType, DateTimeOffset CreatedAt);

public record SearchFilters(ItemType? ItemType, bool? Actionable);

public class SearchService
{
    private readonly MathomDbContext _db;
    public SearchService(MathomDbContext db) => _db = db;

    public async Task<IReadOnlyList<ItemSummary>> TimelineAsync(int take, CancellationToken ct)
    {
        return await _db.Items
            .Where(i => i.Status == ItemStatus.Ready)
            .OrderByDescending(i => i.CreatedAt)
            .Take(take)
            .Select(i => new ItemSummary(i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ItemSummary>> SearchAsync(
        string query, SearchFilters filters, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await TimelineAsync(take, ct);

        var q = _db.Items
            .Where(i => i.Status == ItemStatus.Ready)
            .Where(i => i.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", query)));

        if (filters.ItemType is { } t) q = q.Where(i => i.ItemType == t);
        if (filters.Actionable is { } a) q = q.Where(i => i.Actionable == a);

        return await q
            .OrderByDescending(i => i.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
            .Take(take)
            .Select(i => new ItemSummary(i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt))
            .ToListAsync(ct);
    }
}
