using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

// In production the app sits behind Caddy, which terminates TLS and forwards over plain
// HTTP with X-Forwarded-Proto: https. These tests pin the behaviour that matters: the
// Identity auth (session) cookie — SecurePolicy = SameAsRequest — is flagged Secure only
// when the app believes the request is HTTPS. That happens iff it honours the forwarded
// proto header, which it must do ONLY when ForwardedHeaders:Enabled is set (the platform
// deploy) and NOT on the directly-exposed standalone compose (header would be spoofable).
[Collection("postgres")]
public class ForwardedHeadersTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> CreateApp(bool forwardedHeadersEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", fx.ConnectionString);
            b.UseSetting("ForwardedHeaders:Enabled", forwardedHeadersEnabled ? "true" : "false");
        });

    // Register a fresh user (which issues the auth cookie) while presenting the request as
    // HTTPS via X-Forwarded-Proto, and report whether the auth cookie came back Secure.
    //
    // Cookies are handled manually (HandleCookies = false): once the anti-forgery cookie is
    // flagged Secure, .NET's cookie container would refuse to replay it over the test's actual
    // HTTP transport and the POST's anti-forgery check would fail. Forwarding the cookie by
    // hand sidesteps that test-only artifact (production is HTTPS end-to-end).
    private async Task<bool> AuthCookieSecureAfterRegisterAsync(bool enabled)
    {
        using var app = CreateApp(enabled);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        var getReq = new HttpRequestMessage(HttpMethod.Get, "/Register");
        getReq.Headers.Add("X-Forwarded-Proto", "https");
        var getResp = await client.SendAsync(getReq);
        var html = await getResp.Content.ReadAsStringAsync();
        var token = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var antiforgery = CookiePair(getResp, ".AspNetCore.Antiforgery");
        Assert.NotNull(antiforgery); // sanity: the form must issue the anti-forgery cookie

        var email = "fhdr-" + Guid.NewGuid().ToString("N") + "@example.com";
        var postReq = new HttpRequestMessage(HttpMethod.Post, "/Register")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("Input.Email", email),
                new KeyValuePair<string, string>("Input.Password", "password1"),
            }),
        };
        postReq.Headers.Add("X-Forwarded-Proto", "https");
        postReq.Headers.Add("Cookie", antiforgery);

        var resp = await client.SendAsync(postReq);
        var auth = SetCookies(resp).FirstOrDefault(c =>
            c.Contains(".AspNetCore.Identity.Application", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(auth); // sanity: registration must issue the auth cookie
        return auth!.Contains("secure", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Set-Cookie", out var values) ? values : Enumerable.Empty<string>();

    // The "name=value" head of the first Set-Cookie whose name starts with the prefix.
    private static string? CookiePair(HttpResponseMessage resp, string namePrefix) =>
        SetCookies(resp)
            .FirstOrDefault(c => c.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            ?.Split(';')[0];

    [Fact]
    public async Task Enabled_HonorsXForwardedProto_AuthCookieIsSecure()
    {
        Assert.True(await AuthCookieSecureAfterRegisterAsync(enabled: true));
    }

    [Fact]
    public async Task Disabled_IgnoresXForwardedProto_AuthCookieNotSecure()
    {
        Assert.False(await AuthCookieSecureAfterRegisterAsync(enabled: false));
    }

    // GET /Register renders a form, which issues the anti-forgery cookie. Its SecurePolicy
    // is SameAsRequest, so it tracks the (forwarded) scheme just like the auth cookie.
    private async Task<bool> AntiforgeryCookieSecureAsync(bool enabled)
    {
        using var app = CreateApp(enabled);
        var client = app.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/Register");
        req.Headers.Add("X-Forwarded-Proto", "https");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var af = SetCookies(resp).FirstOrDefault(c =>
            c.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(af); // sanity: the form must issue the anti-forgery cookie
        return af!.Contains("secure", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_HonorsXForwardedProto_AntiforgeryCookieIsSecure()
    {
        Assert.True(await AntiforgeryCookieSecureAsync(enabled: true));
    }

    [Fact]
    public async Task Disabled_IgnoresXForwardedProto_AntiforgeryCookieNotSecure()
    {
        Assert.False(await AntiforgeryCookieSecureAsync(enabled: false));
    }
}
