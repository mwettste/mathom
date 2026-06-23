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
public class AdminAreaTests
{
    private readonly PostgresFixture _fx;
    public AdminAreaTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(_fx.ConnectionString);
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

        // Approve the pending user.
        var approve = await admin.PostAsync($"/Admin/Users?handler=Approve&id={TestUsers.PendingId}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
        }));
        Assert.True(approve.IsSuccessStatusCode);
        await using (var db = _fx.NewDbContext())
            Assert.True(await db.Users.Where(u => u.Id == TestUsers.PendingId).Select(u => u.IsApproved).FirstAsync());

        // Admin cannot revoke their own account.
        await admin.PostAsync($"/Admin/Users?handler=Revoke&id={TestUsers.AdminId}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
        }));
        await using (var db = _fx.NewDbContext())
            Assert.True(await db.Users.Where(u => u.Id == TestUsers.AdminId).Select(u => u.IsApproved).FirstAsync());
    }
}
