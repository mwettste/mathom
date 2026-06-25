// src/Mathom.Web/Pages/Settings.cshtml.cs
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Languages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class SettingsModel(UserLanguageService languages) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public IReadOnlyList<UserLanguageView> Languages { get; private set; } = new List<UserLanguageView>();
    public IReadOnlyList<Locale> Catalog { get; } = Locales.All;

    public async Task OnGetAsync(CancellationToken ct)
        => Languages = await languages.GetViewsAsync(UserId, ct);

    public async Task<IActionResult> OnPostAddAsync(string? locale, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(locale)) await languages.AddAsync(UserId, locale!, ct);
        Languages = await languages.GetViewsAsync(UserId, ct);
        return Partial("Shared/_LanguageList", Languages);
    }

    public async Task<IActionResult> OnPostRemoveAsync(string locale, CancellationToken ct)
    {
        await languages.RemoveAsync(UserId, locale, ct);
        Languages = await languages.GetViewsAsync(UserId, ct);
        return Partial("Shared/_LanguageList", Languages);
    }

    public async Task<IActionResult> OnPostSetPrimaryAsync(string locale, CancellationToken ct)
    {
        await languages.SetPrimaryAsync(UserId, locale, ct);
        Languages = await languages.GetViewsAsync(UserId, ct);
        return Partial("Shared/_LanguageList", Languages);
    }
}
