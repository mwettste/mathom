using System.Security.Claims;
using System.Text.Encodings.Web;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Mathom.Web.Auth;

public static class DeviceTokenDefaults
{
    public const string Scheme = "DeviceToken";
    public const string SessionIdClaim = "mathom:session_id";
    public const string DeviceKindClaim = "mathom:device_kind";
}

// Authenticates `Authorization: Bearer <access token>` against device sessions.
// Challenge is the base-class default: a plain 401, never a redirect.
public class DeviceTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeviceSessionService sessions,
    UserManager<ApplicationUser> users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string Bearer = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header[Bearer.Length..].Trim();
        if (token.Length == 0) return AuthenticateResult.Fail("Empty bearer token.");

        var session = await sessions.ValidateAccessTokenAsync(token, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid access token.");

        var user = await users.FindByIdAsync(session.UserId);
        if (user?.Email is not { } email) return AuthenticateResult.Fail("Unknown user.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId),
            new Claim(ClaimTypes.Name, email),
            new Claim(DeviceTokenDefaults.SessionIdClaim, session.SessionId.ToString()),
            new Claim(DeviceTokenDefaults.DeviceKindClaim, session.Kind.ToString().ToLowerInvariant()),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
