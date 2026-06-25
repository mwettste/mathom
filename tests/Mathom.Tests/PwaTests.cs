using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PwaTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> CreateAppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    [Fact]
    public async Task Manifest_IsServed_WithManifestContentType()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/manifest+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"start_url\": \"/Capture\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Icon_IsServed()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().GetAsync("/icon-192.png");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Layout_LinksManifestAndAppleIcon()
    {
        using var app = await CreateAppAsync();
        var html = await app.CreateClient().GetStringAsync("/Capture");
        Assert.Contains("rel=\"manifest\" href=\"/manifest.webmanifest\"", html);
        Assert.Contains("apple-touch-icon", html);
    }

    [Fact]
    public async Task ServiceWorker_IsServed_AsJavaScript()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var ct = resp.Content.Headers.ContentType?.MediaType;
        Assert.True(ct == "text/javascript" || ct == "application/javascript", $"unexpected content-type: {ct}");
    }

    [Fact]
    public async Task Layout_RegistersServiceWorker()
    {
        using var app = await CreateAppAsync();
        var html = await app.CreateClient().GetStringAsync("/Capture");
        Assert.Contains("serviceWorker", html);
        Assert.Contains("/sw.js", html);
    }
}
