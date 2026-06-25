using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Contexts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class ContextsModel(ContextService contexts) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<ContextView> Items { get; private set; } = new List<ContextView>();
    public Guid? CurrentContextId { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(string? name, CancellationToken ct)
    {
        await contexts.CreateAsync(UserId, name, ct);
        await LoadAsync(ct);
        return Partial("Shared/_ContextList", this);
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid id, string? name, CancellationToken ct)
    {
        await contexts.RenameAsync(UserId, id, name, ct);
        await LoadAsync(ct);
        return Partial("Shared/_ContextList", this);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await contexts.DeleteAsync(UserId, id, ct);
        await LoadAsync(ct);
        return Partial("Shared/_ContextList", this);
    }

    public async Task<IActionResult> OnPostSetCurrentAsync(Guid? contextId, CancellationToken ct)
    {
        await contexts.SetCurrentAsync(UserId, contextId, ct);
        // Full navigation so the whole shell (timeline + switcher) reflects the new context.
        Response.Headers["HX-Redirect"] = "/";
        return new OkResult();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Items = await contexts.ListAsync(UserId, ct);
        CurrentContextId = await contexts.GetCurrentAsync(UserId, ct);
    }
}
