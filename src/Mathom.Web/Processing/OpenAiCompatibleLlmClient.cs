using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

/// <summary>
/// Shared OpenAI-compatible chat-completions logic. Provider-specific subclasses
/// supply the config section name and default base URL; this class owns the HTTP
/// request/response cycle and JSON parsing — it is NOT duplicated per provider.
/// </summary>
public abstract class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _providerName;

    protected OpenAiCompatibleLlmClient(HttpClient http, IConfiguration config, string configSection, string defaultBaseUrl)
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

    public async Task<CleanupResult> CleanupAsync(string rawText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException(
                $"LLM model is not configured for '{_providerName}'. Set {_providerName}:Model and {_providerName}:ApiKey " +
                $"via configuration — e.g. appsettings.Development.json, or the {_providerName.Replace(":", "__")}__Model environment variable.");

        var payload = new
        {
            model = _model,
            response_format = BuildResponseFormat(),
            messages = new object[]
            {
                new { role = "system", content = CleanupPromptBuilder.BuildSystemPrompt() },
                new { role = "user", content = CleanupPromptBuilder.BuildUserPrompt(rawText) }
            }
        };

        using var resp = await _http.PostAsJsonAsync("chat/completions", payload, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var content = doc.RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        return CleanupResultParser.Parse(content);
    }

    /// <summary>
    /// The OpenAI <c>response_format</c> object. Defaults to <c>json_object</c> (works on most
    /// providers, e.g. OpenRouter); a provider can override to require a JSON schema.
    /// </summary>
    protected virtual object BuildResponseFormat() => new { type = "json_object" };
}
