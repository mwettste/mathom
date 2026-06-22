using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Notes;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class NoteModel : PageModel
{
    private readonly SearchService _search;
    private readonly NoteService _notes;
    public NoteModel(SearchService search, NoteService notes)
    {
        _search = search;
        _notes = notes;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public ItemDetail? Item { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Item = await _search.GetAsync(UserId, id, ct);
        if (Item is null) return NotFound();
        return Page();
    }

    // Polled by HTMX while the note is still processing.
    public async Task<IActionResult> OnGetContentAsync(Guid id, CancellationToken ct)
    {
        Item = await _search.GetAsync(UserId, id, ct);
        if (Item is null) return NotFound();
        return Partial("Shared/_NoteContent", Item);
    }

    // Swaps the read view into the inline edit form.
    public async Task<IActionResult> OnGetEditAsync(Guid id, CancellationToken ct)
    {
        Item = await _search.GetAsync(UserId, id, ct);
        if (Item is null || Item.Status != ItemStatus.Ready) return NotFound();
        return Partial("Shared/_NoteEdit", Item);
    }

    public async Task<IActionResult> OnPostEditAsync(
        Guid id, string? title, string? body, string? type, bool actionable, string? tags, CancellationToken ct)
    {
        var itemType = Enum.TryParse<ItemType>(type, ignoreCase: true, out var t) ? (ItemType?)t : null;
        var tagNames = (tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ok = await _notes.UpdateAsync(UserId, id, title, body, itemType, actionable, tagNames, ct);
        if (!ok) return NotFound();

        Item = await _search.GetAsync(UserId, id, ct);
        return Partial("Shared/_NoteContent", Item!);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var ok = await _notes.SoftDeleteAsync(UserId, id, ct);
        if (!ok) return NotFound();
        // HTMX honors HX-Redirect with a full client-side navigation (a 302 would be
        // swapped into the form instead). The header value is the timeline.
        Response.Headers["HX-Redirect"] = "/";
        return new OkResult();
    }
}
