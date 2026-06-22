using System;
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
public class ReprocessTests
{
    private readonly PostgresFixture _fx;
    public ReprocessTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    private async Task<Guid> SeedReadyAsync(string userId, string title)
    {
        await using var db = _fx.NewDbContext();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = title, CleanText = title, Title = title, ItemType = ItemType.Note,
            CreatedAt = DateTimeOffset.UtcNow, ProcessedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString(), UserId = userId,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Reprocess_SetsNoteInFlight()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var id = await SeedReadyAsync(TestUsers.AliceId, "reprocess-me");

        var noteHtml = await client.GetStringAsync($"/Note/{id}");
        var resp = await client.PostAsync($"/Note/{id}?handler=Reprocess", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(noteHtml)),
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fx.NewDbContext();
        Assert.Equal(ItemStatus.Pending, (await db.Items.FirstAsync(i => i.Id == id)).Status);
    }

    [Fact]
    public async Task Reprocess_OtherUsersNote_NotFound()
    {
        using var app = await AppAsync();
        var bob = app.CreateClient();
        bob.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.BobId);
        var id = await SeedReadyAsync(TestUsers.AliceId, "alice-note");

        // Need a valid token; Bob fetches the timeline to get one.
        var page = await bob.GetStringAsync("/");
        var resp = await bob.PostAsync($"/Note/{id}?handler=Reprocess", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(page)),
        }));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
