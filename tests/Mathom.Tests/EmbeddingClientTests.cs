using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mathom.Tests;

public class EmbeddingClientTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Fact]
    public async Task Parses_embedding_from_openai_shape()
    {
        var http = new HttpClient(new StubHandler("{\"data\":[{\"embedding\":[0.1,0.2,0.3]}]}"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embeddings:OpenRouter:Model"] = "test-model",
            ["Embeddings:OpenRouter:BaseUrl"] = "https://example.test/v1/",
        }).Build();
        var client = new OpenRouterEmbeddingClient(http, config);

        var vec = await client.EmbedAsync("hello", CancellationToken.None);

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, vec);
        Assert.Equal("test-model", client.ModelId);
    }

    private sealed class ThrowingClient : IEmbeddingClient
    {
        public string ModelId => "throws";
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => throw new InvalidOperationException("down");
    }

    private sealed class OkClient : IEmbeddingClient
    {
        public string ModelId => "ok";
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(new[] { 1f, 2f });
    }

    [Fact]
    public async Task Fallback_uses_second_provider_when_first_fails()
    {
        var client = new FallbackEmbeddingClient(
            new IEmbeddingClient[] { new ThrowingClient(), new OkClient() },
            NullLogger<FallbackEmbeddingClient>.Instance,
            retryDelay: TimeSpan.Zero);

        var vec = await client.EmbedAsync("x", CancellationToken.None);

        Assert.Equal(new[] { 1f, 2f }, vec);
        Assert.Equal("throws", client.ModelId); // ModelId reports the primary provider
    }
}
