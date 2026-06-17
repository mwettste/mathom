using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PagesIntegrationTests
{
    private readonly PostgresFixture _fx;
    public PagesIntegrationTests(PostgresFixture fx) => _fx = fx;

    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
        });

    [Fact]
    public async Task Timeline_Renders()
    {
        using var app = CreateApp();
        var resp = await app.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Mathom", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SearchHandler_ReturnsPartial()
    {
        using var app = CreateApp();
        var resp = await app.CreateClient().GetAsync("/?handler=Search&q=nothingmatchesthis");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Nothing here yet.", body);
    }
}
