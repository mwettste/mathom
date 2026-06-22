using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Processing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Mathom.Tests;

public class InfomaniakTranscriberTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public string SubmitBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Method == HttpMethod.Post)
            {
                SubmitBody = await req.Content!.ReadAsStringAsync(ct);
                return Json("{\"batch_id\":\"b1\"}");
            }
            return Json("{\"status\":\"success\",\"data\":\"{\\\"text\\\":\\\"hello\\\"}\"}");
        }
        private static HttpResponseMessage Json(string s) =>
            new(HttpStatusCode.OK) { Content = new StringContent(s) };
    }

    private static InfomaniakTranscriber Build(StubHandler h)
    {
        var http = new HttpClient(h) { BaseAddress = new Uri("https://t.local/1/ai/1/openai/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stt:Infomaniak:Model"] = "whisper",
        }).Build();
        return new InfomaniakTranscriber(http, config);
    }

    private static Stream Audio() => new MemoryStream(new byte[] { 1, 2, 3 });

    // .NET 10 omits quotes around name values in multipart headers (name=prompt not name="prompt").
    // We check for both forms so the test is forward-compatible and also works if .NET ever restores quotes.
    private static bool ContainsPromptField(string body) =>
        body.Contains("name=\"prompt\"") || body.Contains("name=prompt");

    [Fact]
    public async Task NonEmptyGlossary_AddsPromptField()
    {
        var h = new StubHandler();
        var t = Build(h);
        await t.TranscribeAsync(Audio(), "note.webm", new List<string> { "Obersaxen" }, CancellationToken.None);
        Assert.True(ContainsPromptField(h.SubmitBody), $"Expected prompt field in body: {h.SubmitBody}");
        Assert.Contains("Obersaxen", h.SubmitBody);
    }

    [Fact]
    public async Task EmptyGlossary_NoPromptField()
    {
        var h = new StubHandler();
        var t = Build(h);
        await t.TranscribeAsync(Audio(), "note.webm", Array.Empty<string>(), CancellationToken.None);
        Assert.False(ContainsPromptField(h.SubmitBody), $"Expected no prompt field in body: {h.SubmitBody}");
    }
}
