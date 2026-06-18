using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class IndexModel : PageModel
{
    private readonly SearchService _search;
    public IndexModel(SearchService search) => _search = search;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<ItemSummary> Items { get; private set; } = new List<ItemSummary>();

    public async Task OnGetAsync(CancellationToken ct)
        => Items = await _search.TimelineAsync(UserId, 50, ct);

    // Polled by HTMX while in-flight items exist, so transcribing/processing entries
    // advance to Ready without a manual reload.
    public async Task<IActionResult> OnGetTimelineAsync(CancellationToken ct)
    {
        Items = await _search.TimelineAsync(UserId, 50, ct);
        return Partial("Shared/_ItemList", Items);
    }

    public async Task<IActionResult> OnGetSearchAsync(string? q, CancellationToken ct)
    {
        Items = string.IsNullOrWhiteSpace(q)
            ? await _search.TimelineAsync(UserId, 50, ct)
            : await _search.SearchAsync(UserId, q, new SearchFilters(null, null), 50, ct);
        return Partial("Shared/_ItemList", Items);
    }
}
