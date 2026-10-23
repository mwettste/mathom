using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Auth;
using Mathom.Web.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class SettingsPageTests(PostgresFixture fx)
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Settings_AddLanguage_ReturnsListPartial_WithLocale()
    {
        await using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var client = app.CreateClient();

        var page = await client.GetStringAsync("/Settings");
        var resp = await client.PostAsync("/Settings?handler=Add", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string, string>("locale", "de-CH"),
        }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("German (Switzerland)", html);
    }

    private static HttpClient As(TestWebAppFactory app, string userId)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add(TestUsers.Header, userId);
        return c;
    }

    private static async Task<IssuedTokens> SeedDeviceAsync(TestWebAppFactory app, string userId, string name)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeviceSessionService>()
            .CreateAsync(userId, name, DeviceKind.Phone, null, CancellationToken.None);
    }

    private static async Task<ValidatedSession?> ValidateAsync(TestWebAppFactory app, string accessToken)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeviceSessionService>()
            .ValidateAccessTokenAsync(accessToken, CancellationToken.None);
    }

    private static Task<HttpResponseMessage> RevokeAsync(HttpClient client, string page, Guid id) =>
        client.PostAsync($"/Settings?handler=RevokeDevice&id={id}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", Token(page)),
        }));

    [Fact]
    public async Task Settings_ListsOwnDevices_NotOthers()
    {
        await using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        await SeedDeviceAsync(app, TestUsers.AliceId, "Alice iPhone");
        await SeedDeviceAsync(app, TestUsers.BobId, "Bob Watch");

        var html = await As(app, TestUsers.AliceId).GetStringAsync("/Settings");

        Assert.Contains("Alice iPhone", html);
        Assert.DoesNotContain("Bob Watch", html);
    }

    [Fact]
    public async Task Settings_EncodesDeviceName()
    {
        await using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        await SeedDeviceAsync(app, TestUsers.AliceId, "<script>alert(1)</script>");

        var html = await As(app, TestUsers.AliceId).GetStringAsync("/Settings");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Settings_RevokeDevice_RemovesItFromList_AndRevokesSession()
    {
        await using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var tokens = await SeedDeviceAsync(app, TestUsers.AliceId, "Alice iPhone");
        var client = As(app, TestUsers.AliceId);

        var page = await client.GetStringAsync("/Settings");
        var resp = await RevokeAsync(client, page, tokens.SessionId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Alice iPhone", html);
        Assert.Contains("No devices signed in.", html);
        Assert.Null(await ValidateAsync(app, tokens.AccessToken));
    }

    [Fact]
    public async Task Settings_RevokeOtherUsersDevice_DoesNothing()
    {
        await using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var bob = await SeedDeviceAsync(app, TestUsers.BobId, "Bob Watch");
        var client = As(app, TestUsers.AliceId);

        var page = await client.GetStringAsync("/Settings");
        var resp = await RevokeAsync(client, page, bob.SessionId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(await ValidateAsync(app, bob.AccessToken));
    }
}
