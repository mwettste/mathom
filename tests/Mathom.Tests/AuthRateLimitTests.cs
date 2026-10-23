using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mathom.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class AuthRateLimitTests(PostgresFixture fx)
{
    private static HttpClient Raw(DeviceAuthTestApp app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> ApiLoginAsync(
        HttpClient client, string email, string? ipHeader = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password = "wrong-password", deviceName = "iPhone", deviceKind = "phone" }),
        };
        if (ipHeader is not null) req.Headers.Add("CF-Connecting-IP", ipHeader);
        return client.SendAsync(req);
    }

    private static string UnknownEmail() => $"{Guid.NewGuid():N}@example.com";

    private static async Task AssertRateLimitedAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("60", resp.Headers.GetValues("Retry-After").Single());
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task ApiLogin_11thAttemptWithinAMinute_429_RateLimited()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var client = Raw(app);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await ApiLoginAsync(client, UnknownEmail())).StatusCode);

        await AssertRateLimitedAsync(await ApiLoginAsync(client, UnknownEmail()));
    }

    [Fact]
    public async Task WebLogin_11thPost_429()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var client = Raw(app);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client)).StatusCode);

        var limited = await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());

        // GET /Login is not limited.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Login")).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync("/Login", new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Input.Email", UnknownEmail()),
            new("Input.Password", "wrong"),
        }));
    }

    [Fact]
    public async Task Refresh_31stWithinAMinute_429()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString);
        var client = Raw(app);
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "bogus" })).StatusCode);

        await AssertRateLimitedAsync(
            await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "bogus" }));
    }

    [Fact]
    public async Task ClientIpHeader_Configured_PartitionsByHeader()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString, s => s.Configure<AuthRateLimitOptions>(o =>
        {
            o.LoginPermitsPerMinute = 1;
            o.ClientIpHeader = "CF-Connecting-IP";
        }));
        var client = Raw(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiLoginAsync(client, UnknownEmail(), "1.1.1.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiLoginAsync(client, UnknownEmail(), "2.2.2.2")).StatusCode);
        await AssertRateLimitedAsync(await ApiLoginAsync(client, UnknownEmail(), "1.1.1.1"));
    }

    [Fact]
    public async Task ClientIpHeader_NotConfigured_IgnoresHeader()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString, s => s.Configure<AuthRateLimitOptions>(o =>
            o.LoginPermitsPerMinute = 1));
        var client = Raw(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiLoginAsync(client, UnknownEmail(), "1.1.1.1")).StatusCode);
        await AssertRateLimitedAsync(await ApiLoginAsync(client, UnknownEmail(), "2.2.2.2"));
    }

    [Fact]
    public async Task ClientIpHeader_Ipv6InSameSlash64_SharesBucket()
    {
        using var app = new DeviceAuthTestApp(fx.ConnectionString, s => s.Configure<AuthRateLimitOptions>(o =>
        {
            o.LoginPermitsPerMinute = 1;
            o.ClientIpHeader = "CF-Connecting-IP";
        }));
        var client = Raw(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiLoginAsync(client, UnknownEmail(), "2001:db8:1:2::1")).StatusCode);
        await AssertRateLimitedAsync(await ApiLoginAsync(client, UnknownEmail(), "2001:db8:1:2:ffff::9"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiLoginAsync(client, UnknownEmail(), "2001:db8:1:3::1")).StatusCode);
    }
}
