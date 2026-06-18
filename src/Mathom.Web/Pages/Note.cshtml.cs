using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class NoteModel : PageModel
{
    private readonly SearchService _search;
    public NoteModel(SearchService search) => _search = search;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public ItemDetail? Item { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Item = await _search.GetAsync(UserId, id, ct);
        if (Item is null) return NotFound();
        return Page();
    }

    // Polled by HTMX while the note is still processing, so it fills in without a reload.
    public async Task<IActionResult> OnGetContentAsync(Guid id, CancellationToken ct)
    {
        Item = await _search.GetAsync(UserId, id, ct);
        if (Item is null) return NotFound();
        return Partial("Shared/_NoteContent", Item);
    }
}
