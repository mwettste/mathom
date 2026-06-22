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
public class GlossaryModel : PageModel
{
    private readonly GlossaryService _glossary;
    public GlossaryModel(GlossaryService glossary) => _glossary = glossary;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<(Guid Id, string Term)> Terms { get; private set; } = new List<(Guid, string)>();

    public async Task OnGetAsync(CancellationToken ct)
        => Terms = await _glossary.GetTermRowsAsync(UserId, ct);

    public async Task<IActionResult> OnPostAddAsync(string? term, CancellationToken ct)
    {
        await _glossary.AddAsync(UserId, term ?? string.Empty, null, ct);
        Terms = await _glossary.GetTermRowsAsync(UserId, ct);
        return Partial("Shared/_GlossaryList", Terms);
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken ct)
    {
        await _glossary.RemoveAsync(UserId, id, ct);
        Terms = await _glossary.GetTermRowsAsync(UserId, ct);
        return Partial("Shared/_GlossaryList", Terms);
    }
}
