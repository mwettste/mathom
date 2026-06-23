using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages;

[Authorize]
public class PendingModel : PageModel
{
    private readonly UserAdminService _userAdmin;
    public PendingModel(UserAdminService userAdmin) => _userAdmin = userAdmin;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        => await _userAdmin.IsApprovedAsync(UserId, ct) ? Redirect("/") : Page();
}
