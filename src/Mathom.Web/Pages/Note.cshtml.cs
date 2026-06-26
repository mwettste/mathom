using System;
using System.Collections.Generic;
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
public class NoteModel(SearchService search, NoteService notes, Mathom.Web.Contexts.ContextService contexts) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public ItemDetail? Item { get; private set; }

    public IReadOnlyList<Mathom.Web.Contexts.ContextView> Contexts { get; private set; } = new List<Mathom.Web.Contexts.ContextView>();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Item = await search.GetAsync(UserId, id, ct);
        if (Item is null) return NotFound();
        Contexts = await contexts.ListAsync(UserId, ct);
        return Page();
    }

    // Polled by HTMX while the note is still processing.
    public async Task<IActionResult> OnGetContentAsync(Guid id, CancellationToken ct)
    {
        Item = await search.GetAsync(UserId, id, ct);
        if (Item is null) return NotFound();
        return Partial("Shared/_NoteContent", Item);
    }

    // Swaps the read view into the inline edit form.
    public async Task<IActionResult> OnGetEditAsync(Guid id, CancellationToken ct)
    {
        Item = await search.GetAsync(UserId, id, ct);
        if (Item is null || Item.Status != ItemStatus.Ready) return NotFound();
        return Partial("Shared/_NoteEdit", Item);
    }

    public async Task<IActionResult> OnPostEditAsync(
        Guid id, string? title, string? body, string? type, bool actionable, string? tags, CancellationToken ct)
    {
        var itemType = Enum.TryParse<ItemType>(type, ignoreCase: true, out var t) ? (ItemType?)t : null;
        var tagNames = (tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ok = await notes.UpdateAsync(UserId, id, title, body, itemType, actionable, tagNames, ct);
        if (!ok) return NotFound();

        Item = await search.GetAsync(UserId, id, ct);
        return Partial("Shared/_NoteContent", Item!);
    }

    public async Task<IActionResult> OnPostReprocessAsync(Guid id, CancellationToken ct)
    {
        var ok = await notes.ReprocessAsync(UserId, id, ct);
        if (!ok) return NotFound();
        Item = await search.GetAsync(UserId, id, ct);
        return Partial("Shared/_NoteContent", Item!);
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid id, Guid? contextId, CancellationToken ct)
    {
        var ok = await notes.MoveAsync(UserId, id, contextId, ct);
        if (!ok) return NotFound();
        // The note is now Pending again; reload the detail and swap the content partial.
        Item = await search.GetAsync(UserId, id, ct);
        return Partial("Shared/_NoteContent", Item!);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var ok = await notes.SoftDeleteAsync(UserId, id, ct);
        if (!ok) return NotFound();
        // HTMX honors HX-Redirect with a full client-side navigation (a 302 would be
        // swapped into the form instead). The header value is the timeline.
        Response.Headers["HX-Redirect"] = "/";
        return new OkResult();
    }
}
