using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Glossary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class GlossaryModel(GlossaryService glossary, Mathom.Web.Contexts.ContextService contexts) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<GlossaryTermView> Terms { get; private set; } = new List<GlossaryTermView>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var ctxId = await contexts.GetCurrentAsync(UserId, ct);
        Terms = await glossary.GetTermViewsAsync(UserId, ctxId, ct);
    }

    public async Task<IActionResult> OnPostAddAsync(string? term, string? variant, CancellationToken ct)
    {
        var ctxId = await contexts.GetCurrentAsync(UserId, ct);
        await glossary.AddAsync(UserId, ctxId, term ?? string.Empty, variant, ct);
        Terms = await glossary.GetTermViewsAsync(UserId, ctxId, ct);
        return Partial("Shared/_GlossaryList", Terms);
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken ct)
    {
        var ctxId = await contexts.GetCurrentAsync(UserId, ct);
        await glossary.RemoveAsync(UserId, id, ct);
        Terms = await glossary.GetTermViewsAsync(UserId, ctxId, ct);
        return Partial("Shared/_GlossaryList", Terms);
    }

    public async Task<IActionResult> OnPostRemoveVariantAsync(Guid id, CancellationToken ct)
    {
        var ctxId = await contexts.GetCurrentAsync(UserId, ct);
        await glossary.RemoveVariantAsync(UserId, id, ct);
        Terms = await glossary.GetTermViewsAsync(UserId, ctxId, ct);
        return Partial("Shared/_GlossaryList", Terms);
    }

    public async Task<IActionResult> OnGetEditDescriptionAsync(Guid id, CancellationToken ct)
    {
        var d = await glossary.GetDescriptionAsync(UserId, id, ct);
        if (d is null) return NotFound();
        return Partial("Shared/_GlossaryDescriptionEdit", d);
    }

    public async Task<IActionResult> OnGetShowDescriptionAsync(Guid id, CancellationToken ct)
    {
        var d = await glossary.GetDescriptionAsync(UserId, id, ct);
        if (d is null) return NotFound();
        return Partial("Shared/_GlossaryDescription", d);
    }

    public async Task<IActionResult> OnPostSetDescriptionAsync(Guid id, string? description, CancellationToken ct)
    {
        if (!await glossary.SetDescriptionAsync(UserId, id, description, ct)) return NotFound();
        var d = await glossary.GetDescriptionAsync(UserId, id, ct);
        return Partial("Shared/_GlossaryDescription", d!);
    }
}
