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

public class OpenRouterImageReaderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Body = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"shopping list: milk, eggs\"}}]}")
            };
        }
    }

    private static OpenRouterImageReader Build(StubHandler h)
    {
        var http = new HttpClient(h) { BaseAddress = new Uri("https://openrouter.local/api/v1/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vision:OpenRouter:Model"] = "some/vision-model",
        }).Build();
        return new OpenRouterImageReader(http, config);
    }

    private static ImageData Jpeg() => new(new MemoryStream(new byte[] { 1, 2, 3 }), "note.jpg");

    [Fact]
    public async Task Reads_Content_From_ChatCompletion()
    {
        var h = new StubHandler();
        var text = await Build(h).ExtractAsync(new[] { Jpeg() }, Array.Empty<string>(), CancellationToken.None);
        Assert.Equal("shopping list: milk, eggs", text);
    }

    [Fact]
    public async Task Sends_Model_ImageDataUrl_And_Glossary()
    {
        var h = new StubHandler();
        await Build(h).ExtractAsync(new[] { Jpeg() }, new List<string> { "Obersaxen" }, CancellationToken.None);
        Assert.Contains("some/vision-model", h.Body);
        Assert.Contains("image_url", h.Body);
        Assert.Contains("data:image/jpeg;base64,", h.Body);
        Assert.Contains("Obersaxen", h.Body);
    }

    [Fact]
    public async Task Throws_When_Model_Not_Configured()
    {
        var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://openrouter.local/api/v1/") };
        var config = new ConfigurationBuilder().Build();
        var reader = new OpenRouterImageReader(http, config);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ExtractAsync(new[] { Jpeg() }, Array.Empty<string>(), CancellationToken.None));
    }
}
