using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Mathom.Web.Capture;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

file record IdResponse(Guid Id);

[Collection("postgres")]
public class UserIsolationTests(PostgresFixture fx)
{
    private static HttpClient As(TestWebAppFactory app, string userId)
    {
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestUsers.Header, userId);
        return c;
    }

    [Fact]
    public async Task UserB_CannotSeeOrFetch_UserA_Item()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();

        // Alice captures.
        var alice = As(app, TestUsers.AliceId);
        var created = await alice.PostAsJsonAsync("/capture",
            new CaptureRequest("alice-secret-note", Guid.NewGuid().ToString()));
        var id = (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;

        // Alice can open her own note.
        var aliceNote = await alice.GetAsync($"/Note/{id}");
        Assert.Equal(HttpStatusCode.OK, aliceNote.StatusCode);

        // Bob cannot fetch it (404, not 403 — no existence leak).
        var bob = As(app, TestUsers.BobId);
        var bobNote = await bob.GetAsync($"/Note/{id}");
        Assert.Equal(HttpStatusCode.NotFound, bobNote.StatusCode);

        // Bob's timeline partial does not contain Alice's text.
        var bobTimeline = await bob.GetStringAsync("/?handler=Timeline");
        Assert.DoesNotContain("alice-secret-note", bobTimeline);
    }

    // Exercises the REAL production cookie auth (no Test scheme override), so the
    // assertion proves the OnRedirectToLogin override returns 401 for /capture
    // while Razor pages still redirect (302) to /Login.
    [Fact]
    public async Task Capture_WithoutAuth_Returns401_NotRedirect()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", fx.ConnectionString);
        });
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // POST to the capture API with no auth cookie -> 401 (not a redirect).
        var resp = await client.PostAsJsonAsync("/capture",
            new CaptureRequest("nope", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        // Unauthenticated GET of a Razor page -> 302 redirect to /Login.
        var pageResp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, pageResp.StatusCode);
        Assert.Contains("/Login", pageResp.Headers.Location!.OriginalString);
    }

    // Regression: unauthenticated GET /Capture (the PWA start_url) must redirect
    // to /Login, not return a bare 401.  The OnRedirectToLogin guard was
    // case-insensitive, so /Capture matched the /capture API check and got 401.
    // Fix: gate the 401 on HttpMethods.IsPost so the page falls through to 302.
    [Fact]
    public async Task CapturePage_WithoutAuth_RedirectsToLogin()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", fx.ConnectionString);
        });
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // GET /Capture (the PWA start_url) with no auth cookie -> 302 to /Login.
        var resp = await client.GetAsync("/Capture");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Login", resp.Headers.Location!.OriginalString);

        // POST /capture still returns 401 (not a redirect) so fetch/offline-replay works.
        var apiResp = await client.PostAsJsonAsync("/capture",
            new CaptureRequest("nope", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, apiResp.StatusCode);
    }
}
