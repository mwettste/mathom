using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class PagesIntegrationTests
{
    private readonly PostgresFixture _fx;
    public PagesIntegrationTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> CreateAppAsync()
    {
        var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    [Fact]
    public async Task Timeline_Renders()
    {
        using var app = await CreateAppAsync();
        var resp = await app.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Mathom", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Search_NoMatch_RendersEmptyState()
    {
        using var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        var resp = await app.CreateClient().GetAsync("/?q=nothingmatchesthis");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("No notes match", body);
    }
}
