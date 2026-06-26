using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class CapturePageTests(PostgresFixture fx)
{
    [Fact]
    public async Task CapturePage_RendersTextAndVoiceModes()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var resp = await app.CreateClient().GetAsync("/Capture");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Speak", html);             // voice mode heading
        Assert.Contains("Record", html);            // record button label
        Assert.Contains("capture.js", html);        // Alpine components (voice posts to /capture/voice)
        Assert.Contains("alpine.min.js", html);     // Alpine runtime (from layout)
    }

    [Fact]
    public async Task CapturePage_RendersPhotoMode()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var html = await app.CreateClient().GetStringAsync("/Capture");
        Assert.Contains("Photo", html);                 // photo card heading
        Assert.Contains("photoCapture()", html);        // Alpine component wired up
        Assert.Contains("/capture/photo", html);        // upload target referenced in markup
    }

    [Fact]
    public async Task CapturePage_PhotoInput_AllowsGalleryAndContext()
    {
        using var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        var client = app.CreateClient();
        var html = await (await client.GetAsync("/Capture")).Content.ReadAsStringAsync();

        Assert.Contains("image/*,application/octet-stream", html);   // forces legacy Camera/Gallery/Files chooser
        Assert.DoesNotContain("capture=\"environment\"", html);       // no camera-only lock
        Assert.Contains("photoContext", html);                        // x-model name for the context textarea
    }
}
