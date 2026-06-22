using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class EditDeleteTests
{
    private readonly PostgresFixture _fx;
    public EditDeleteTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(_fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    private async Task<Guid> SeedReadyAsync(string title)
    {
        await using var db = _fx.NewDbContext();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = SourceType.Text,
            RawText = title, CleanText = title, Title = title, ItemType = ItemType.Note,
            CreatedAt = DateTimeOffset.UtcNow, ProcessedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString(), UserId = TestUsers.AliceId,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Edit_UpdatesNote()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var id = await SeedReadyAsync("before-edit");

        var formHtml = await client.GetStringAsync($"/Note/{id}?handler=Edit");
        var token = Token(formHtml);
        Assert.False(string.IsNullOrEmpty(token));

        var resp = await client.PostAsync($"/Note/{id}?handler=Edit", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", token),
            new KeyValuePair<string,string>("Title", "after-edit"),
            new KeyValuePair<string,string>("Body", "edited body"),
            new KeyValuePair<string,string>("Type", "task"),
            new KeyValuePair<string,string>("Actionable", "true"),
            new KeyValuePair<string,string>("Tags", "alpha, beta"),
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("after-edit", await resp.Content.ReadAsStringAsync());

        await using var db = _fx.NewDbContext();
        var saved = await db.Items.Include(i => i.ItemTags).ThenInclude(t => t.Tag).FirstAsync(i => i.Id == id);
        Assert.Equal("after-edit", saved.Title);
        Assert.Equal(ItemType.Task, saved.ItemType);
        Assert.True(saved.Actionable);
        Assert.Equal(2, saved.ItemTags.Count);
    }

    [Fact]
    public async Task Delete_SoftDeletes_RemovesFromTimeline()
    {
        using var app = await AppAsync();
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = await SeedReadyAsync("delete-me");

        var noteHtml = await client.GetStringAsync($"/Note/{id}");
        var token = Token(noteHtml);

        var resp = await client.PostAsync($"/Note/{id}?handler=Delete", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", token),
        }));
        // The handler signals HTMX to navigate to the timeline via HX-Redirect.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("/", resp.Headers.GetValues("HX-Redirect").Single());

        var timeline = await client.GetStringAsync("/");
        Assert.DoesNotContain("delete-me", timeline);

        await using var db = _fx.NewDbContext();
        Assert.NotNull((await db.Items.IgnoreQueryFilters().FirstAsync(i => i.Id == id)).DeletedAt);
    }

    [Fact]
    public async Task Edit_OtherUsersNote_NotFound()
    {
        using var app = await AppAsync();
        var bob = app.CreateClient();
        bob.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.BobId);
        var id = await SeedReadyAsync("alice-note");  // owned by Alice

        var resp = await bob.GetAsync($"/Note/{id}?handler=Edit");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
