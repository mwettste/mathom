using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Mathom.Web.Auth;

public class AuthRateLimitOptions
{
    public int LoginPermitsPerMinute { get; set; } = 10;
    public int RefreshPermitsPerMinute { get; set; } = 30;

    // Header that carries the real client IP (e.g. CF-Connecting-IP). Null = ignore all headers.
    // Only set this where nothing but a trusted edge can reach the app; otherwise clients can spoof it.
    public string? ClientIpHeader { get; set; }
}

public static class AuthRateLimiting
{
    public const string LoginPolicy = "auth-login";
    public const string RefreshPolicy = "auth-refresh";

    public static string ClientKey(HttpContext ctx)
    {
        var header = ctx.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value.ClientIpHeader;
        if (!string.IsNullOrWhiteSpace(header)
            && ctx.Request.Headers.TryGetValue(header, out var value)
            && !string.IsNullOrWhiteSpace(value.ToString()))
            return value.ToString().Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuthRateLimitOptions>(config.GetSection("AuthRateLimit"));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Only POSTs (credential submissions) count; GET /Login renders a form and stays free.
            o.AddPolicy(LoginPolicy, ctx => Partition(ctx, LoginPolicy, opts => opts.LoginPermitsPerMinute));
            o.AddPolicy(RefreshPolicy, ctx => Partition(ctx, RefreshPolicy, opts => opts.RefreshPermitsPerMinute));
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                http.Response.Headers.RetryAfter = "60";
                if (http.Request.Path.StartsWithSegments("/api"))
                {
                    await ApiError.WriteAsync(http, StatusCodes.Status429TooManyRequests, "rate_limited",
                        "Too many requests. Try again in a minute.");
                }
                else
                {
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    http.Response.ContentType = "text/plain";
                    await http.Response.WriteAsync("Too many requests. Try again in a minute.", ct);
                }
            };
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(
        HttpContext ctx, string policy, Func<AuthRateLimitOptions, int> permits)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) return RateLimitPartition.GetNoLimiter("get");
        var limit = permits(ctx.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{ClientKey(ctx)}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }
}
