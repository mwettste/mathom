using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class AdminAreaTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    private static HttpClient As(TestWebAppFactory app, string userId)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add(TestUsers.Header, userId);
        return c;
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task NonAdmin_Forbidden_Admin_Ok()
    {
        using var app = await AppAsync();
        var alice = await As(app, TestUsers.AliceId).GetAsync("/Admin/Users");
        Assert.Equal(HttpStatusCode.Forbidden, alice.StatusCode);

        var admin = await As(app, TestUsers.AdminId).GetAsync("/Admin/Users");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Admin_Approves_And_CannotRevokeSelf()
    {
        using var app = await AppAsync();
        var admin = As(app, TestUsers.AdminId);
        var page = await admin.GetStringAsync("/Admin/Users");
        var token = Token(page);
        Assert.NotEmpty(token);

        // Approve the pending user.
        var approve = await admin.PostAsync($"/Admin/Users?handler=Approve&id={TestUsers.PendingId}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", token),
        }));
        Assert.True(approve.IsSuccessStatusCode);
        await using (var db = fx.NewDbContext())
            Assert.True(await db.Users.Where(u => u.Id == TestUsers.PendingId).Select(u => u.IsApproved).FirstAsync());

        // Admin cannot revoke their own account.
        var revoke = await admin.PostAsync($"/Admin/Users?handler=Revoke&id={TestUsers.AdminId}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", token),
        }));
        Assert.True(revoke.IsSuccessStatusCode);
        await using (var db = fx.NewDbContext())
            Assert.True(await db.Users.Where(u => u.Id == TestUsers.AdminId).Select(u => u.IsApproved).FirstAsync());
    }
}
