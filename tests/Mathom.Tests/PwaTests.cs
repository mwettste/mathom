using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PwaTests
{
    private readonly PostgresFixture _fx;
    public PwaTests(PostgresFixture fx) => _fx = fx;

    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
        });

    [Fact]
    public async Task Manifest_IsServed_WithManifestContentType()
    {
        using var app = CreateApp();
        var resp = await app.CreateClient().GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/manifest+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"start_url\": \"/Capture\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Icon_IsServed()
    {
        using var app = CreateApp();
        var resp = await app.CreateClient().GetAsync("/icon-192.png");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Layout_LinksManifestAndAppleIcon()
    {
        using var app = CreateApp();
        var html = await app.CreateClient().GetStringAsync("/Capture");
        Assert.Contains("rel=\"manifest\" href=\"/manifest.webmanifest\"", html);
        Assert.Contains("apple-touch-icon", html);
    }
}
