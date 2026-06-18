using System.Net;
using System.Threading.Tasks;
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
        using var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        var resp = await app.CreateClient().GetAsync("/Capture");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Speak", html);             // voice mode heading
        Assert.Contains("Record", html);            // record button label
        Assert.Contains("capture.js", html);        // Alpine components (voice posts to /capture/voice)
        Assert.Contains("alpine.min.js", html);     // Alpine runtime (from layout)
    }
}
