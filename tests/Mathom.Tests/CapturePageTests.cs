using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class CapturePageTests
{
    private readonly PostgresFixture _fx;
    public CapturePageTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task CapturePage_RendersTextAndVoiceModes()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
            b.UseEnvironment("Testing");
        });
        var resp = await app.CreateClient().GetAsync("/Capture");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("/capture/voice", html);
        Assert.Contains("alpine.min.js", html);
        Assert.Contains("Record", html);
    }
}
