using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class IndexModel(SearchService search) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<ItemSummary> Items { get; private set; } = new List<ItemSummary>();

    public string? Q { get; private set; }
    public string? Tag { get; private set; }
    public ItemType? Type { get; private set; }
    public bool? Actionable { get; private set; }

    public bool HasFilters => Tag is not null || Type is not null || Actionable is not null;
    public string? TypeParam => Type?.ToString().ToLowerInvariant();
    public string? ActionableParam => Actionable is null ? null : (Actionable.Value ? "true" : "false");

    public static IReadOnlyList<ItemType> AllTypes { get; } =
        new[] { ItemType.Idea, ItemType.Task, ItemType.Note, ItemType.Reference, ItemType.Journal };

    public string ToggleType(ItemType t) => Build(Q, Tag, Type == t ? null : t, Actionable);
    public string ToggleActionable() => Build(Q, Tag, Type, Actionable == true ? null : true);
    public string WithoutTag() => Build(Q, null, Type, Actionable);
    public string WithoutType() => Build(Q, Tag, null, Actionable);
    public string WithoutActionable() => Build(Q, Tag, Type, null);

    private static string Build(string? q, string? tag, ItemType? type, bool? actionable)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(q)) qs.Add("q=" + Uri.EscapeDataString(q));
        if (!string.IsNullOrWhiteSpace(tag)) qs.Add("tag=" + Uri.EscapeDataString(tag));
        if (type is not null) qs.Add("type=" + type.Value.ToString().ToLowerInvariant());
        if (actionable is not null) qs.Add("actionable=" + (actionable.Value ? "true" : "false"));
        return qs.Count == 0 ? "/" : "/?" + string.Join("&", qs);
    }

    public async Task OnGetAsync(string? q, string? tag, string? type, bool? actionable, CancellationToken ct)
        => await LoadAsync(q, tag, type, actionable, ct);

    // Polled by HTMX while in-flight items exist — always the full, unfiltered timeline.
    public async Task<IActionResult> OnGetTimelineAsync(CancellationToken ct)
    {
        Items = await search.TimelineAsync(UserId, 50, ct);
        return Partial("Shared/_ItemList", Items);
    }

    private async Task LoadAsync(string? q, string? tag, string? type, bool? actionable, CancellationToken ct)
    {
        Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        Type = Enum.TryParse<ItemType>(type, ignoreCase: true, out var t) ? t : null;
        Actionable = actionable;

        Items = await search.QueryAsync(UserId, Q, new SearchFilters(Type, Actionable, Tag), 50, ct);
    }
}
