using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

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
    Guid? ContextId,
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

public class SearchService(
    MathomDbContext db,
    IEmbeddingClient embeddings,
    ILogger<SearchService> logger)
{
    private const int CandidateK = 50;

    // Full single item for the detail page (includes the raw transcript and any error).
    public async Task<ItemDetail?> GetAsync(string userId, Guid id, CancellationToken ct)
    {
        return await db.Items
            .Where(i => i.Id == id && i.UserId == userId)
            .Select(i => new ItemDetail(
                i.Id, i.ContextId, i.Title, i.CleanText, i.RawText, i.ItemType, i.SourceType,
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

    // Unified, user-scoped (and context-scoped) query. With no text query and no filters it is
    // the timeline (all statuses, newest first). A text query restricts to Ready + hybrid
    // lexical+semantic ranking via Reciprocal Rank Fusion. Type/Actionable/Tag filters narrow further.
    public async Task<IReadOnlyList<ItemSummary>> QueryAsync(
        string userId, Guid? contextId, string? q, SearchFilters filters, int take, CancellationToken ct)
    {
        var baseItems = db.Items.Where(i => i.UserId == userId);
        baseItems = contextId is { } cid ? baseItems.Where(i => i.ContextId == cid) : baseItems.Where(i => i.ContextId == null);

        if (filters.ItemType is { } ft) baseItems = baseItems.Where(i => i.ItemType == ft);
        if (filters.Actionable is { } fa) baseItems = baseItems.Where(i => i.Actionable == fa);
        if (!string.IsNullOrWhiteSpace(filters.Tag))
        {
            var tag = filters.Tag!.ToLower();
            baseItems = baseItems.Where(i => i.ItemTags.Any(it => it.Tag.Name.ToLower() == tag));
        }

        var hasQuery = !string.IsNullOrWhiteSpace(q);
        if (!hasQuery)
        {
            return await Project(baseItems.OrderByDescending(i => i.CreatedAt).Take(take), ct);
        }

        var query = q!;
        var ready = baseItems.Where(i => i.Status == ItemStatus.Ready);

        // Lexical candidates (source + translation variants), ranked by tsvector rank.
        // EF.Functions.WebSearchToTsQuery must be inlined inside each LINQ expression — it cannot
        // be captured into a variable outside a query context.
        var lexical = await ready
            .Where(i => i.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", query))
                     || i.Translations.Any(t => t.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", query))))
            .OrderByDescending(i => i.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("simple", query)))
            .Take(CandidateK)
            .Select(i => i.Id)
            .ToListAsync(ct);

        // Semantic candidates, ranked by cosine distance — only if we can embed the query.
        var semantic = new List<Guid>();
        Vector? queryVector = null;
        try
        {
            queryVector = new Vector(await embeddings.EmbedAsync(query, ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query embedding failed; falling back to lexical-only search.");
        }

        if (queryVector is not null)
        {
            var ranked = await ready
                .Where(i => i.Embedding != null)
                .Select(i => new { i.Id, Distance = i.Embedding!.CosineDistance(queryVector) })
                .OrderBy(x => x.Distance)
                .Take(CandidateK)
                .ToListAsync(ct);
            semantic = ranked.Select(x => x.Id).ToList();
        }

        var fused = ReciprocalRankFusion(lexical, semantic);
        var topIds = fused.Take(take).ToList();

        logger.LogDebug(
            "Hybrid search user={User} q={Query} model={Model} lexical={LexCount} semantic={SemCount} returned={Returned}",
            userId, query, embeddings.ModelId, lexical.Count, semantic.Count, topIds.Count);

        if (topIds.Count == 0) return new List<ItemSummary>();

        var summaries = await Project(baseItems.Where(i => topIds.Contains(i.Id)), ct);
        var order = topIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        return summaries.OrderBy(s => order[s.Id]).ToList();
    }

    // Reciprocal Rank Fusion: score(d) = Σ 1 / (k + rank). Robust to the two signals' different
    // score scales; an item in both lists is boosted.
    private static List<Guid> ReciprocalRankFusion(IReadOnlyList<Guid> a, IReadOnlyList<Guid> b, int k = 60)
    {
        var scores = new Dictionary<Guid, double>();
        static void Accumulate(Dictionary<Guid, double> s, IReadOnlyList<Guid> list, int k)
        {
            for (var rank = 0; rank < list.Count; rank++)
                s[list[rank]] = s.GetValueOrDefault(list[rank]) + 1.0 / (k + rank + 1);
        }
        Accumulate(scores, a, k);
        Accumulate(scores, b, k);
        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    private static async Task<List<ItemSummary>> Project(IQueryable<Item> items, CancellationToken ct)
    {
        return await items
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList(),
                i.SourceLanguage,
                i.Translations.Select(t => new TranslationSummary(t.Locale, t.Title, t.CleanText)).ToList()))
            .ToListAsync(ct);
    }
}
