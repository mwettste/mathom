using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages.Admin;

[Authorize(Roles = "Admin")]
public class UsersModel(UserAdminService users) : PageModel
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public IReadOnlyList<UserRow> Users { get; private set; } = new List<UserRow>();

    public async Task OnGetAsync(CancellationToken ct) => Users = await users.ListUsersAsync(ct);

    public async Task<IActionResult> OnPostApproveAsync(string id, CancellationToken ct)
    {
        await users.ApproveAsync(id, ct);
        Users = await users.ListUsersAsync(ct);
        return Partial("Shared/_UsersList", Users);
    }

    public async Task<IActionResult> OnPostRevokeAsync(string id, CancellationToken ct)
    {
        await users.RevokeAsync(UserId, id, ct);   // self-revoke rejected in the service
        Users = await users.ListUsersAsync(ct);
        return Partial("Shared/_UsersList", Users);
    }
}
