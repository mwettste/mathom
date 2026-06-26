using System;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Capture;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Mathom.Web.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

using Microsoft.Extensions.Logging.Abstractions;

namespace Mathom.Tests;

[Collection("postgres")]
public class EndToEndTests(PostgresFixture fx)
{
    private record IdResponse(Guid Id);

    [Fact]
    public async Task Capture_Process_Search_Roundtrip()
    {
        var fake = new FakeLlmClient
        {
            Respond = _ => new CleanupResult(
                "Garden plan",
                "Plant tomatoes in spring.",
                ItemType.Idea,
                true,
                new[] { new CleanupTag("gardening", TagKind.Topic) })
        };

        using var app = new TestWebAppFactory(fx.ConnectionString, s =>
        {
            s.RemoveAll(typeof(ILlmClient));
            s.AddScoped<ILlmClient>(_ => fake);
            s.AddHostedService<ProcessingWorker>(); // exactly one worker, from this registration
        });
        await app.SeedUsersAsync();

        var client = app.CreateClient();
        var resp = await client.PostAsJsonAsync("/capture",
            new CaptureRequest("tomatoes spring plant", Guid.NewGuid().ToString()));
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<IdResponse>();
        var id = body!.Id;

        // Poll the SPECIFIC captured item by id; always check deadline before sleeping.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        ItemStatus status = ItemStatus.Pending;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            await using var db = fx.NewDbContext();
            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id);
            if (item is not null)
            {
                status = item.Status;
                if (status is ItemStatus.Ready or ItemStatus.Failed)
                    break;
            }
        }

        Assert.Equal(ItemStatus.Ready, status);

        await using var verify = fx.NewDbContext();
        var results = await new SearchService(verify, new FakeEmbeddingClient(), NullLogger<SearchService>.Instance)
            .SearchAsync(TestUsers.AliceId, null, "tomatoes", new SearchFilters(null, null), 50, CancellationToken.None);
        Assert.Contains(results, r => r.Id == id);
    }
}
