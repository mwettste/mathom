using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Admin;

public static class AdminBootstrap
{
    public const string AdminRole = "Admin";

    // The admin allowlist: the single prod AdminEmail plus any preview-only secret
    // emails (PreviewAdminEmails, comma-separated). PreviewAdminEmails is set ONLY by
    // the preview workflow, never in the shared prod .env, so in production this is just
    // { AdminEmail }. Trimmed, blank-dropped, deduped case-insensitively.
    public static IReadOnlyList<string> AdminEmailsFromConfig(IConfiguration config)
    {
        var emails = new List<string>();
        Add(config["AdminEmail"]);
        foreach (var e in (config["PreviewAdminEmails"] ?? string.Empty).Split(','))
            Add(e);
        return emails;

        void Add(string? raw)
        {
            var e = raw?.Trim();
            if (string.IsNullOrEmpty(e)) return;
            if (!emails.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
                emails.Add(e);
        }
    }

    // Ensures the Admin role exists and, for each email naming an existing user,
    // ensures that user is approved and in the Admin role. Idempotent.
    public static async Task EnsureRoleAndPromoteAsync(
        RoleManager<IdentityRole> roles, UserManager<ApplicationUser> users,
        IEnumerable<string> adminEmails, CancellationToken ct = default)
    {
        if (!await roles.RoleExistsAsync(AdminRole))
            await roles.CreateAsync(new IdentityRole(AdminRole));

        foreach (var email in adminEmails)
        {
            if (string.IsNullOrWhiteSpace(email)) continue;
            var admin = await users.FindByEmailAsync(email);
            if (admin is null) continue;

            if (!admin.IsApproved)
            {
                admin.IsApproved = true;
                await users.UpdateAsync(admin);
            }
            if (!await users.IsInRoleAsync(admin, AdminRole))
                await users.AddToRoleAsync(admin, AdminRole);
        }
    }
}
