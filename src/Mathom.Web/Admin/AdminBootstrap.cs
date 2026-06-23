using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;

namespace Mathom.Web.Admin;

public static class AdminBootstrap
{
    public const string AdminRole = "Admin";

    // Ensures the Admin role exists and, if adminEmail names an existing user,
    // ensures that user is approved and in the Admin role. Idempotent.
    public static async Task EnsureRoleAndPromoteAsync(
        RoleManager<IdentityRole> roles, UserManager<ApplicationUser> users,
        string? adminEmail, CancellationToken ct = default)
    {
        if (!await roles.RoleExistsAsync(AdminRole))
            await roles.CreateAsync(new IdentityRole(AdminRole));

        if (string.IsNullOrWhiteSpace(adminEmail)) return;
        var admin = await users.FindByEmailAsync(adminEmail);
        if (admin is null) return;

        if (!admin.IsApproved)
        {
            admin.IsApproved = true;
            await users.UpdateAsync(admin);
        }
        if (!await users.IsInRoleAsync(admin, AdminRole))
            await users.AddToRoleAsync(admin, AdminRole);
    }
}
