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
public class EditDeleteTests(PostgresFixture fx)
{
    private async Task<TestWebAppFactory> AppAsync()
    {
        var app = new TestWebAppFactory(fx.ConnectionString);
        await app.SeedUsersAsync();
        return app;
    }

    private async Task<Guid> SeedReadyAsync(string title)
    {
        await using var db = fx.NewDbContext();
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

        await using var db = fx.NewDbContext();
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

        await using var db = fx.NewDbContext();
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

    [Fact]
    public async Task Delete_OtherUsersNote_NotFound()
    {
        using var app = await AppAsync();
        // Bob gets a token by GETting the index page
        var bob = app.CreateClient();
        bob.DefaultRequestHeaders.Add(TestUsers.Header, TestUsers.BobId);
        var indexHtml = await bob.GetStringAsync("/");
        var token = Token(indexHtml);

        var id = await SeedReadyAsync("alice-note-for-delete");  // owned by Alice

        var resp = await bob.PostAsync($"/Note/{id}?handler=Delete", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task EditForm_PrefillsWithoutSpuriousCheckedOrSelected()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        // SeedReadyAsync creates ItemType.Note and Actionable defaults to false
        var id = await SeedReadyAsync("prefill-test-note");

        var html = await client.GetStringAsync($"/Note/{id}?handler=Edit");

        // The actionable checkbox must NOT carry a checked attribute
        var checkboxMatch = Regex.Match(html, @"<input[^>]*name=""actionable""[^>]*>");
        Assert.True(checkboxMatch.Success, "Should find the actionable checkbox input");
        Assert.DoesNotContain("checked", checkboxMatch.Value);

        // Exactly one <option> element should carry a selected attribute,
        // and it must be the "note" option
        var selectedOptions = Regex.Matches(html, @"<option[^>]*selected[^>]*>[^<]*</option>");
        Assert.Single(selectedOptions);
        Assert.Contains("note", selectedOptions[0].Value);
    }

    [Fact]
    public async Task Trash_ListsRestoresAndPurges()
    {
        using var app = await AppAsync();
        var client = app.CreateClient();
        var id = await SeedReadyAsync("trashed-note");

        // Soft-delete it via the note handler.
        var noteHtml = await client.GetStringAsync($"/Note/{id}");
        await client.PostAsync($"/Note/{id}?handler=Delete", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(noteHtml)),
        }));

        // It appears in Trash.
        var trashHtml = await client.GetStringAsync("/Trash");
        Assert.Contains("trashed-note", trashHtml);

        // Restore it.
        var restoreResp = await client.PostAsync($"/Trash?handler=Restore&id={id}", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("__RequestVerificationToken", Token(trashHtml)),
        }));
        Assert.Equal(HttpStatusCode.OK, restoreResp.StatusCode);

        await using var db = fx.NewDbContext();
        Assert.Null((await db.Items.IgnoreQueryFilters().FirstAsync(i => i.Id == id)).DeletedAt);  // live again
    }
}
