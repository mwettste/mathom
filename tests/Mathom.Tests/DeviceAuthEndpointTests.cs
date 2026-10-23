using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Auth;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class DeviceAuthEndpointTests(PostgresFixture fx)
{
    private static HttpClient Raw(DeviceAuthTestApp app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> LoginAsync(
        DeviceAuthTestApp app, string email, string password, string name = "iPhone", string kind = "phone") =>
        Raw(app).PostAsJsonAsync("/api/v1/auth/login", new { email, password, deviceName = name, deviceKind = kind });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertErrorAsync(HttpResponseMessage resp, HttpStatusCode status, string code)
    {
        Assert.Equal(status, resp.StatusCode);
        var json = await JsonAsync(resp);
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetString()));
    }

    private static async Task<int> ActiveSessionCountAsync(DeviceAuthTestApp app, string userId)
    {
        using var scope = app.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<DeviceSessionService>();
        return (await sessions.ListActiveAsync(userId, CancellationToken.None)).Count;
    }

    private static async Task SetApprovedAsync(DeviceAuthTestApp app, string userId, bool approved)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId) ?? throw new InvalidOperationException();
        user.IsApproved = approved;
        var result = await users.UpdateAsync(user);
        Assert.True(result.Succeeded);
    }

    private static async Task<(string Access, string Refresh, Guid SessionId)> LoginOkAsync(
        DeviceAuthTestApp app, string email, string password, string name = "iPhone", string kind = "phone")
    {
        var resp = await LoginAsync(app, email, password, name, kind);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await JsonAsync(resp);
        return (json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!,
            json.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task Login_Succeeds_AndTokenWorksOnMe()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);

        var resp = await LoginAsync(app, email, password);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await JsonAsync(resp);
        Assert.False(string.IsNullOrEmpty(json.GetProperty("refreshToken").GetString()));
        Assert.True(json.GetProperty("accessTokenExpiresAt").GetDateTimeOffset() > app.Time.GetUtcNow());
        var me = await app.Bearer(json.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(userId, (await JsonAsync(me)).GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Login_EmailIsCaseInsensitive()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(approved: true);

        var resp = await LoginAsync(app, email.ToUpperInvariant(), password);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_401_InvalidCredentials()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, _, _) = await app.CreateUserAsync(approved: true);
        await AssertErrorAsync(await LoginAsync(app, email, "wrong-password"),
            HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task Login_UnknownEmail_401_InvalidCredentials()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        await AssertErrorAsync(await LoginAsync(app, "nobody@example.com", "password1"),
            HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task Login_LockedOut_After10Failures_429()
    {
        // Measure lockout, not the rate limiter.
        using var app = new DeviceAuthTestApp(fx.ConnectionString,
            s => s.Configure<AuthRateLimitOptions>(o => o.LoginPermitsPerMinute = 100));
        var (email, password, _) = await app.CreateUserAsync(approved: true);
        for (var i = 0; i < 9; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(app, email, "wrong-password")).StatusCode);
        // The 10th failure trips the lockout, so it is already answered with 429.
        await AssertErrorAsync(await LoginAsync(app, email, "wrong-password"), HttpStatusCode.TooManyRequests, "locked_out");

        await AssertErrorAsync(await LoginAsync(app, email, password), HttpStatusCode.TooManyRequests, "locked_out");
    }

    [Fact]
    public async Task Login_Unapproved_403_PendingApproval_NoSessionRow()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: false);

        await AssertErrorAsync(await LoginAsync(app, email, password), HttpStatusCode.Forbidden, "pending_approval");

        Assert.Equal(0, await ActiveSessionCountAsync(app, userId));
    }

    [Fact]
    public async Task Login_WatchKind_400_InvalidDeviceKind()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);

        await AssertErrorAsync(await LoginAsync(app, email, password, kind: "watch"),
            HttpStatusCode.BadRequest, "invalid_device_kind");

        Assert.Equal(0, await ActiveSessionCountAsync(app, userId));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Login_BlankDeviceName_400_NoSessionRow(string name)
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);

        await AssertErrorAsync(await LoginAsync(app, email, password, name),
            HttpStatusCode.BadRequest, "invalid_device_name");

        Assert.Equal(0, await ActiveSessionCountAsync(app, userId));
    }

    [Fact]
    public async Task Login_TooLongDeviceName_400_NoSessionRow()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);

        await AssertErrorAsync(await LoginAsync(app, email, password, new string('x', 101)),
            HttpStatusCode.BadRequest, "invalid_device_name");

        Assert.Equal(0, await ActiveSessionCountAsync(app, userId));
    }

    [Fact]
    public async Task Refresh_ReturnsNewPair_ThatWorks()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(approved: true);
        var first = await LoginOkAsync(app, email, password);

        var resp = await Raw(app).PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.Refresh });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await JsonAsync(resp);
        Assert.NotEqual(first.Refresh, json.GetProperty("refreshToken").GetString());
        Assert.Equal(first.SessionId, json.GetProperty("sessionId").GetGuid());
        var me = await app.Bearer(json.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Refresh_Unknown_401_InvalidRefreshToken()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var resp = await Raw(app).PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "nope" });
        await AssertErrorAsync(resp, HttpStatusCode.Unauthorized, "invalid_refresh_token");
    }

    [Fact]
    public async Task Refresh_UserUnapproved_403_AndOldRefreshTokenStillUnused()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);
        var first = await LoginOkAsync(app, email, password);
        await SetApprovedAsync(app, userId, false);

        var denied = await Raw(app).PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.Refresh });
        await AssertErrorAsync(denied, HttpStatusCode.Forbidden, "pending_approval");

        // Past the grace window: had the denied call rotated the token, reuse would now be rejected.
        app.Time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await SetApprovedAsync(app, userId, true);
        var ok = await Raw(app).PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.Refresh });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Login_WithBearerOfUnapprovedUser_IsNotGated()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (emailA, passwordA, userA) = await app.CreateUserAsync(approved: true);
        var (emailB, passwordB, _) = await app.CreateUserAsync(approved: true);
        var a = await LoginOkAsync(app, emailA, passwordA);
        await SetApprovedAsync(app, userA, false);

        var client = app.Bearer(a.Access);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = emailB, password = passwordB, deviceName = "iPhone", deviceKind = "phone" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = a.Refresh });
        await AssertErrorAsync(refresh, HttpStatusCode.Forbidden, "pending_approval");
    }

    [Fact]
    public async Task Bearer_UserUnapprovedAfterLogin_403_PendingApprovalJson()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);
        var session = await LoginOkAsync(app, email, password);
        await SetApprovedAsync(app, userId, false);

        var resp = await app.Bearer(session.Access).GetAsync("/api/v1/me");

        await AssertErrorAsync(resp, HttpStatusCode.Forbidden, "pending_approval");
        Assert.Null(resp.Headers.Location);
    }

    [Fact]
    public async Task Logout_RevokesOnlyCallingSession()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(approved: true);
        var a = await LoginOkAsync(app, email, password, "A");
        var b = await LoginOkAsync(app, email, password, "B");

        var resp = await app.Bearer(a.Access).PostAsync("/api/v1/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Bearer(a.Access).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Bearer(b.Access).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Devices_FromPhone_CreatesWatchSession_IndependentOfPhone()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, userId) = await app.CreateUserAsync(approved: true);
        var phone = await LoginOkAsync(app, email, password);

        var resp = await app.Bearer(phone.Access).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "  Watch  " });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var watch = await JsonAsync(resp);
        Assert.NotEqual(phone.SessionId, watch.GetProperty("sessionId").GetGuid());

        using (var scope = app.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<DeviceSessionService>();
            var views = await sessions.ListActiveAsync(userId, CancellationToken.None);
            var w = Assert.Single(views, v => v.Kind == DeviceKind.Watch);
            Assert.Equal("Watch", w.Name);
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await app.Bearer(phone.Access).PostAsync("/api/v1/auth/logout", null)).StatusCode);
        var me = await app.Bearer(watch.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Devices_FromWatch_403()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(approved: true);
        var phone = await LoginOkAsync(app, email, password);
        var watchResp = await app.Bearer(phone.Access).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Watch" });
        var watchToken = (await JsonAsync(watchResp)).GetProperty("accessToken").GetString()!;

        var resp = await app.Bearer(watchToken).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Another" });

        await AssertErrorAsync(resp, HttpStatusCode.Forbidden, "forbidden_scope");
    }

    [Fact]
    public async Task Devices_FromOtherKind_403()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(approved: true);
        var other = await LoginOkAsync(app, email, password, kind: "other");

        var resp = await app.Bearer(other.Access).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Watch" });

        await AssertErrorAsync(resp, HttpStatusCode.Forbidden, "forbidden_scope");
    }

    [Fact]
    public async Task Watch_OnMe_200_OnUnlistedApiPath_403_ForbiddenScope()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (email, password, _) = await app.CreateUserAsync(approved: true);
        var phone = await LoginOkAsync(app, email, password);
        var watchResp = await app.Bearer(phone.Access).PostAsJsonAsync("/api/v1/auth/devices", new { deviceName = "Watch" });
        var client = app.Bearer((await JsonAsync(watchResp)).GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        await AssertErrorAsync(await client.GetAsync("/api/v1/anything"), HttpStatusCode.Forbidden, "forbidden_scope");
    }

    [Fact]
    public async Task Gate_WebPath_Unapproved_StillRedirectsToPending()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.PendingId);

        var resp = await c.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/Pending", resp.Headers.Location?.OriginalString);
    }
}
