using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Mathom.Web.Processing;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class VoiceEndToEndTests
{
    private readonly PostgresFixture _fx;
    public VoiceEndToEndTests(PostgresFixture fx) => _fx = fx;

    private record IdResponse(Guid Id);

    [Fact]
    public async Task Capture_Voice_Transcribe_Process_Search_Roundtrip()
    {
        var media = new FakeMediaStore();
        var transcriber = new FakeTranscriber { Respond = _ => "remember to water the basil" };
        var llm = new FakeLlmClient
        {
            Respond = raw => new CleanupResult("Water basil", raw.Trim(), ItemType.Task, true,
                new[] { new CleanupTag("garden", TagKind.Topic) })
        };

        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Mathom", _fx.ConnectionString);
            b.UseEnvironment("Testing");
            b.ConfigureServices(s =>
            {
                s.RemoveAll(typeof(IMediaStore));
                s.AddSingleton<IMediaStore>(media);
                s.RemoveAll(typeof(ITranscriber));
                s.AddScoped<ITranscriber>(_ => transcriber);
                s.RemoveAll(typeof(ILlmClient));
                s.AddScoped<ILlmClient>(_ => llm);
                s.AddHostedService<ProcessingWorker>();
            });
        });

        var client = app.CreateClient();

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
        form.Add(file, "audio", "note.webm");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "idempotencyKey");

        var resp = await client.PostAsync("/capture/voice", form);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<IdResponse>();
        var id = body!.Id;

        ItemStatus status = ItemStatus.Pending;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            await using var db = _fx.NewDbContext();
            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id);
            if (item is not null) { status = item.Status; if (status is ItemStatus.Ready or ItemStatus.Failed) break; }
        }

        Assert.Equal(ItemStatus.Ready, status);

        await using var verify = _fx.NewDbContext();
        var stored = await verify.Items.SingleAsync(i => i.Id == id);
        Assert.Equal("remember to water the basil", stored.RawText); // transcript preserved
        var results = await new SearchService(verify).SearchAsync("basil", new SearchFilters(null, null), 50, CancellationToken.None);
        Assert.Contains(results, r => r.Id == id);
    }
}
