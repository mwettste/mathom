using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Auth;
using Mathom.Web.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class DeviceTokenAuthTests(PostgresFixture fx)
{
    private static async Task<(IssuedTokens Tokens, string Email, string UserId)> NewSessionAsync(DeviceAuthTestApp app)
    {
        var (email, _, userId) = await app.CreateUserAsync(approved: true);
        using var scope = app.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<DeviceSessionService>();
        var tokens = await sessions.CreateAsync(userId, "Test phone", DeviceKind.Phone, null, CancellationToken.None);
        return (tokens, email, userId);
    }

    private static HttpClient Raw(DeviceAuthTestApp app) =>
        app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Me_WithValidBearer_ReturnsUser()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (tokens, email, userId) = await NewSessionAsync(app);

        var resp = await app.Bearer(tokens.AccessToken).GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId, json.GetProperty("userId").GetString());
        Assert.Equal(email, json.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401_NoRedirect()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var resp = await Raw(app).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
    }

    [Fact]
    public async Task Me_WithUnknownToken_Returns401()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var resp = await app.Bearer("not-a-real-token").GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Me_WithLowercaseBearerScheme_Works()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (tokens, _, _) = await NewSessionAsync(app);
        var client = Raw(app);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer " + tokens.AccessToken);

        var resp = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Me_WithBasicAuthHeader_Returns401()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (tokens, _, _) = await NewSessionAsync(app);
        var client = Raw(app);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic " + tokens.AccessToken);

        var resp = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Me_WithRevokedSession_Returns401()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var (tokens, _, userId) = await NewSessionAsync(app);
        using (var scope = app.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<DeviceSessionService>();
            await sessions.RevokeAsync(userId, tokens.SessionId, CancellationToken.None);
        }

        var resp = await app.Bearer(tokens.AccessToken).GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Me_WithCookieOnly_Returns401()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var client = Raw(app);
        var html = await client.GetStringAsync("/Register");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var reg = await client.PostAsync("/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = $"{Guid.NewGuid():N}@example.com",
            ["Input.Password"] = "password1",
        }));
        Assert.True(reg.Headers.Contains("Set-Cookie")); // signed in via cookie

        var resp = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WebPage_Anonymous_StillRedirectsToLogin()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var resp = await Raw(app).GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/Login", resp.Headers.Location!.AbsolutePath);
    }
}
