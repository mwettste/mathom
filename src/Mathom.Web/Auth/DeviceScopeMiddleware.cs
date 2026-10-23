using System.Security.Claims;

namespace Mathom.Web.Auth;

// A watch session may only reach capture, /me, refresh and logout. Everything else under /api
// answers 403 forbidden_scope, including paths that do not exist (not a 404).
public class DeviceScopeMiddleware(RequestDelegate next)
{
    private static readonly string[] WatchAllowlist =
        { "/api/v1/capture", "/api/v1/me", "/api/v1/auth/refresh", "/api/v1/auth/logout" };

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/api")
            && ctx.User.FindFirstValue(DeviceTokenDefaults.DeviceKindClaim) == "watch"
            && !IsAllowed(ctx.Request.Path))
        {
            return ApiError.WriteAsync(ctx, StatusCodes.Status403Forbidden, "forbidden_scope",
                "This device is not allowed to use this endpoint.");
        }
        return next(ctx);
    }

    private static bool IsAllowed(PathString path)
    {
        foreach (var p in WatchAllowlist)
            if (path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
