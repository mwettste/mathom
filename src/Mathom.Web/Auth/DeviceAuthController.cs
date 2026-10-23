using System.Security.Claims;
using Mathom.Web.Admin;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Mathom.Web.Auth;

public record LoginRequest(string? Email, string? Password, string? DeviceName, string? DeviceKind);
public record RefreshRequest(string? RefreshToken);
public record ProvisionDeviceRequest(string? DeviceName);

[ApiController]
[Route("api/v1/auth")]
public class DeviceAuthController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    UserAdminService userAdmin,
    DeviceSessionService sessions) : ControllerBase
{
    private const int MaxDeviceNameLength = 100;

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var name = NormalizeName(req.DeviceName);
        if (name is null) return InvalidName();
        var kind = (req.DeviceKind?.Trim().ToLowerInvariant()) switch
        {
            "phone" => (DeviceKind?)DeviceKind.Phone,
            "other" => DeviceKind.Other,
            _ => null,
        };
        if (kind is null)
            return ApiError.Result(StatusCodes.Status400BadRequest, "invalid_device_kind",
                "deviceKind must be 'phone' or 'other'.");

        var user = string.IsNullOrWhiteSpace(req.Email) ? null : await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || string.IsNullOrEmpty(req.Password)) return InvalidCredentials();

        var check = await signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (check.IsLockedOut)
            return ApiError.Result(StatusCodes.Status429TooManyRequests, "locked_out",
                "Too many failed attempts. Try again later.");
        if (!check.Succeeded) return InvalidCredentials();

        if (!await userAdmin.IsApprovedAsync(user.Id, ct)) return ApiError.PendingApproval();

        var tokens = await sessions.CreateAsync(user.Id, name, kind.Value, null, ct);
        return Ok(tokens);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.RefreshToken)) return InvalidRefreshToken();

        // Approval is checked before rotating, so a gated user keeps their still-unused token.
        var userId = await sessions.GetUserIdForRefreshTokenAsync(req.RefreshToken, ct);
        if (userId is null) return InvalidRefreshToken();
        if (!await userAdmin.IsApprovedAsync(userId, ct)) return ApiError.PendingApproval();

        var refreshed = await sessions.RefreshAsync(req.RefreshToken, ct);
        return refreshed is null ? InvalidRefreshToken() : Ok(refreshed.Value.Tokens);
    }

    [HttpPost("devices")]
    [Authorize(Policy = DevicePolicies.PhoneSession)]
    public async Task<IActionResult> ProvisionDevice([FromBody] ProvisionDeviceRequest req, CancellationToken ct)
    {
        var name = NormalizeName(req.DeviceName);
        if (name is null) return InvalidName();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var phoneSessionId = Guid.Parse(User.FindFirstValue(DeviceTokenDefaults.SessionIdClaim)!);
        var tokens = await sessions.CreateAsync(userId, name, DeviceKind.Watch, phoneSessionId, ct);
        return Ok(tokens);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sessionId = Guid.Parse(User.FindFirstValue(DeviceTokenDefaults.SessionIdClaim)!);
        await sessions.RevokeAsync(userId, sessionId, ct);
        return NoContent();
    }

    private static string? NormalizeName(string? raw)
    {
        var name = raw?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > MaxDeviceNameLength ? null : name;
    }

    private static IActionResult InvalidName() =>
        ApiError.Result(StatusCodes.Status400BadRequest, "invalid_device_name",
            $"deviceName is required and at most {MaxDeviceNameLength} characters.");

    private static IActionResult InvalidCredentials() =>
        ApiError.Result(StatusCodes.Status401Unauthorized, "invalid_credentials", "Email or password is wrong.");

    private static IActionResult InvalidRefreshToken() =>
        ApiError.Result(StatusCodes.Status401Unauthorized, "invalid_refresh_token", "The refresh token is not valid.");
}
