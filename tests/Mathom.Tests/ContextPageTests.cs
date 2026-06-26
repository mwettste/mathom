using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ContextPageTests(PostgresFixture fx)
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    [Fact]
    public async Task Create_Then_Page_ShowsContext()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var page = await client.GetStringAsync("/Contexts");

        var resp = await client.PostAsync("/Contexts?handler=Create", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string,string>("name", "Business"),
        }));
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Contains("Business", await resp.Content.ReadAsStringAsync());

        await using var db = fx.NewDbContext();
        Assert.True(await db.Contexts.AnyAsync(c => c.UserId == TestUsers.AliceId && c.Name == "Business"));
    }

    [Fact]
    public async Task SetCurrent_PersistsOnUser()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();

        var ctxId = Guid.NewGuid();
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Mathom.Web.Domain.Context { Id = ctxId, UserId = TestUsers.AliceId, Name = "Work-setcurrent", CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        var page = await client.GetStringAsync("/Contexts");
        var resp = await client.PostAsync("/Contexts?handler=SetCurrent", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
            new KeyValuePair<string,string>("contextId", ctxId.ToString()),
        }));
        Assert.True(resp.IsSuccessStatusCode);

        await using var db = fx.NewDbContext();
        Assert.Equal(ctxId, (await db.Users.SingleAsync(u => u.Id == TestUsers.AliceId)).CurrentContextId);
    }

    [Fact]
    public async Task Timeline_ShowsOnlyCurrentContextItems()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();

        var ctxId = Guid.NewGuid();
        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Mathom.Web.Domain.Context { Id = ctxId, UserId = TestUsers.AliceId, Name = "Work-timeline", CreatedAt = DateTimeOffset.UtcNow });
            var alice = await seed.Users.SingleAsync(u => u.Id == TestUsers.AliceId);
            alice.CurrentContextId = ctxId;
            seed.Items.Add(new Mathom.Web.Domain.Item
            {
                Id = Guid.NewGuid(), Status = Mathom.Web.Domain.ItemStatus.Ready, SourceType = Mathom.Web.Domain.SourceType.Text,
                RawText = "in work", CleanText = "in work", Title = "WORKITEM", ItemType = Mathom.Web.Domain.ItemType.Note,
                CreatedAt = DateTimeOffset.UtcNow, IdempotencyKey = Guid.NewGuid().ToString(),
                UserId = TestUsers.AliceId, ContextId = ctxId,
            });
            seed.Items.Add(new Mathom.Web.Domain.Item
            {
                Id = Guid.NewGuid(), Status = Mathom.Web.Domain.ItemStatus.Ready, SourceType = Mathom.Web.Domain.SourceType.Text,
                RawText = "in inbox", CleanText = "in inbox", Title = "INBOXITEM", ItemType = Mathom.Web.Domain.ItemType.Note,
                CreatedAt = DateTimeOffset.UtcNow, IdempotencyKey = Guid.NewGuid().ToString(),
                UserId = TestUsers.AliceId, ContextId = null,
            });
            await seed.SaveChangesAsync();
        }

        var html = await client.GetStringAsync("/");
        Assert.Contains("WORKITEM", html);
        Assert.DoesNotContain("INBOXITEM", html);
    }
}
