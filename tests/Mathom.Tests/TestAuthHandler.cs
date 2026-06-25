using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mathom.Tests;

public static class TestUsers
{
    public const string Scheme = "Test";
    public const string Header = "X-Test-User";
    public const string AliceId = "test-user-a";
    public const string BobId = "test-user-b";
    public const string AdminId = "test-user-admin";
    public const string PendingId = "test-user-pending";
    public const string AdminEmail = "config-admin@example.com";
}

// Authenticates every request as the user named by the X-Test-User header
// (default Alice). The literal "anonymous" leaves the request unauthenticated
// so 401 behavior can be exercised.
public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[TestUsers.Header].ToString();
        if (string.IsNullOrEmpty(userId)) userId = TestUsers.AliceId;
        if (userId == "anonymous") return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new System.Collections.Generic.List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId + "@example.com"),
        };
        if (userId == TestUsers.AdminId) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, TestUsers.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), TestUsers.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
