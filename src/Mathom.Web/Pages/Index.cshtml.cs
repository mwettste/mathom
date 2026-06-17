using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

public class IndexModel : PageModel
{
    private readonly SearchService _search;
    public IndexModel(SearchService search) => _search = search;

    public IReadOnlyList<ItemSummary> Items { get; private set; } = new List<ItemSummary>();

    public async Task OnGetAsync(CancellationToken ct)
        => Items = await _search.TimelineAsync(50, ct);

    public async Task<IActionResult> OnGetSearchAsync(string? q, CancellationToken ct)
    {
        Items = string.IsNullOrWhiteSpace(q)
            ? await _search.TimelineAsync(50, ct)
            : await _search.SearchAsync(q, new SearchFilters(null, null), 50, ct);
        return Partial("Shared/_ItemList", Items);
    }
}
