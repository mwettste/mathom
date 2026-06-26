using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Embeddings;

/// <summary>
/// Shared OpenAI-compatible <c>/embeddings</c> logic. Mirrors OpenAiCompatibleLlmClient:
/// subclasses supply the config section and default base URL; this class owns the HTTP cycle.
/// </summary>
public abstract class OpenAiCompatibleEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _providerName;

    protected OpenAiCompatibleEmbeddingClient(HttpClient http, IConfiguration config, string configSection, string defaultBaseUrl)
    {
        _http = http;
        _providerName = configSection;
        var section = config.GetSection(configSection);
        _model = section["Model"] ?? string.Empty;
        _http.BaseAddress ??= new Uri(section["BaseUrl"] ?? defaultBaseUrl);
        var key = section["ApiKey"];
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", key);
    }

    public string ModelId => _model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException(
                $"Embedding model is not configured for '{_providerName}'. Set {_providerName}:Model and {_providerName}:ApiKey.");

        var payload = new { model = _model, input = text };
        using var resp = await _http.PostAsJsonAsync("embeddings", payload, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vec = new float[arr.GetArrayLength()];
        var i = 0;
        foreach (var el in arr.EnumerateArray())
            vec[i++] = el.GetSingle();
        return vec;
    }
}
