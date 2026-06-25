using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Notes;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class TrashModel(NoteService notes) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<ItemSummary> Items { get; private set; } = new List<ItemSummary>();

    public async Task OnGetAsync(CancellationToken ct)
        => Items = await notes.TrashAsync(UserId, 100, ct);

    public async Task<IActionResult> OnPostRestoreAsync(Guid id, CancellationToken ct)
    {
        await notes.RestoreAsync(UserId, id, ct);
        Items = await notes.TrashAsync(UserId, 100, ct);
        return Partial("Shared/_TrashList", Items);
    }

    public async Task<IActionResult> OnPostPurgeAsync(Guid id, CancellationToken ct)
    {
        await notes.PurgeAsync(UserId, id, ct);
        Items = await notes.TrashAsync(UserId, 100, ct);
        return Partial("Shared/_TrashList", Items);
    }
}
