using Microsoft.AspNetCore.Mvc;

namespace Mathom.Web.Auth;

// The `{ error, code }` JSON shape every /api error response uses.
public static class ApiError
{
    public static IActionResult Result(int status, string code, string message) =>
        new ObjectResult(new { error = message, code }) { StatusCode = status };

    public static Task WriteAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message, code }, ctx.RequestAborted);
    }

    public static IActionResult PendingApproval() =>
        Result(StatusCodes.Status403Forbidden, "pending_approval", "Your account is waiting for admin approval.");
}
