using System.Net;
using System.Net.Sockets;
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

    // Rate-limit bucket for a request. IPv6 clients are keyed by their /64 prefix: an attacker
    // typically controls a whole /64 (2^64 addresses), so per-address buckets would make the
    // limit useless against password spraying. IPv4-mapped IPv6 collapses to plain IPv4.
    // A configured header that is missing or not a single valid IP is ignored (falls back to
    // the socket address), so spoofed junk cannot mint its own bucket.
    public static string ClientKey(HttpContext ctx)
    {
        var header = ctx.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value.ClientIpHeader;
        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(header)
            && ctx.Request.Headers.TryGetValue(header, out var value)
            && IPAddress.TryParse(value.ToString().Trim(), out var fromHeader))
            ip = fromHeader;
        ip ??= ctx.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return $"{new IPAddress(bytes)}/64";
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
