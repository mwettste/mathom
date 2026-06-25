using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class GatingTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    private static System.Net.Http.HttpClient NoRedirect(TestWebAppFactory app, string userId)
    {
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestUsers.Header, userId);
        return c;
    }

    [Fact]
    public async Task Unapproved_User_IsRedirected_ToPending()
    {
        using var app = await AppAsync();
        var resp = await NoRedirect(app, TestUsers.PendingId).GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/Pending", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Approved_User_ReachesApp()
    {
        using var app = await AppAsync();
        var resp = await NoRedirect(app, TestUsers.AliceId).GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Pending_Page_NotRedirected_ForUnapproved_ButRedirectsApproved()
    {
        using var app = await AppAsync();
        var pendingResp = await NoRedirect(app, TestUsers.PendingId).GetAsync("/Pending");
        Assert.Equal(HttpStatusCode.OK, pendingResp.StatusCode);

        var approvedResp = await NoRedirect(app, TestUsers.AliceId).GetAsync("/Pending");
        Assert.Equal(HttpStatusCode.Redirect, approvedResp.StatusCode);
        Assert.Equal("/", approvedResp.Headers.Location!.OriginalString);
    }
}
