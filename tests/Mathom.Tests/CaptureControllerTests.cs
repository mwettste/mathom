using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Mathom.Web.Capture;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class CaptureControllerTests
{
    private readonly PostgresFixture _fx;
    public CaptureControllerTests(PostgresFixture fx) => _fx = fx;

    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString));

    [Fact]
    public async Task Post_Capture_CreatesPendingItem()
    {
        using var app = CreateApp();
        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/capture", new CaptureRequest("a fresh idea", "idem-1"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = _fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "idem-1");
        Assert.Equal(ItemStatus.Pending, item.Status);
        Assert.Equal("a fresh idea", item.RawText);
        Assert.Equal(SourceType.Text, item.SourceType);
    }

    [Fact]
    public async Task Post_Capture_IsIdempotent()
    {
        using var app = CreateApp();
        var client = app.CreateClient();

        await client.PostAsJsonAsync("/capture", new CaptureRequest("dup", "idem-dup"));
        var second = await client.PostAsJsonAsync("/capture", new CaptureRequest("dup", "idem-dup"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var db = _fx.NewDbContext();
        Assert.Equal(1, await db.Items.CountAsync(i => i.IdempotencyKey == "idem-dup"));
    }

    [Fact]
    public async Task Post_Capture_RejectsEmptyText()
    {
        using var app = CreateApp();
        var client = app.CreateClient();
        var resp = await client.PostAsJsonAsync("/capture", new CaptureRequest("   ", "idem-empty"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
