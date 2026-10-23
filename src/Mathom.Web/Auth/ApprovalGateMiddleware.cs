using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Mathom.Web.Admin;
using Microsoft.AspNetCore.Http;

namespace Mathom.Web.Auth;

// Redirects authenticated-but-unapproved users to /Pending for any non-allowlisted path;
// /api paths get a 403 pending_approval JSON error instead.
public class ApprovalGateMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowlist = { "/Pending", "/Login", "/Register", "/Logout", "/healthz" };

    public async Task InvokeAsync(HttpContext ctx, UserAdminService userAdmin)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && !IsAllowed(ctx.Request.Path))
        {
            var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id is not null && !await userAdmin.IsApprovedAsync(id, ctx.RequestAborted))
            {
                // API clients get JSON, not a redirect to an HTML page.
                if (ctx.Request.Path.StartsWithSegments("/api"))
                    await ApiError.WriteAsync(ctx, StatusCodes.Status403Forbidden, "pending_approval",
                        "Your account is waiting for admin approval.");
                else
                    ctx.Response.Redirect("/Pending");
                return;
            }
        }
        await next(ctx);
    }

    private static bool IsAllowed(PathString path)
    {
        foreach (var p in Allowlist)
            if (path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
