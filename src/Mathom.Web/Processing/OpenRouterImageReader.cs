using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

/// <summary>
/// Reads images via an OpenRouter (OpenAI-compatible) vision chat-completion. Each image is
/// inlined as a base64 <c>data:</c> URL alongside a text instruction; the model's reply text
/// becomes the item's RawText. Config section <c>Vision:OpenRouter</c> (Model/ApiKey/BaseUrl).
/// </summary>
public class OpenRouterImageReader : IImageReader
{
    private const string Instruction =
        "Read these images. Transcribe ALL text faithfully and verbatim, preserving meaningful " +
        "structure (lists, headings, line breaks). For non-text content (diagrams, sketches, charts), " +
        "add a brief description so it is searchable. Output only the extracted/described content — " +
        "no preamble, commentary, or explanations.";

    private readonly HttpClient _http;
    private readonly string _model;

    public OpenRouterImageReader(HttpClient http, IConfiguration config)
    {
        _http = http;
        var section = config.GetSection("Vision:OpenRouter");
        _model = section["Model"] ?? string.Empty;
        _http.BaseAddress ??= new Uri(section["BaseUrl"] ?? "https://openrouter.ai/api/v1/");
        var key = section["ApiKey"];
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", key);
    }

    public async Task<string> ExtractAsync(IReadOnlyList<ImageData> images, IReadOnlyList<string> glossary, string? context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException(
                "Vision model is not configured for 'Vision:OpenRouter'. Set Vision:OpenRouter:Model and " +
                "Vision:OpenRouter:ApiKey via configuration — e.g. appsettings.Development.json, or the " +
                "Vision__OpenRouter__Model environment variable.");

        var text = Instruction;
        if (glossary is { Count: > 0 })
            text += " Known terms / likely spellings: " + string.Join(", ", glossary) + ".";
        if (!string.IsNullOrWhiteSpace(context))
            text += " User-supplied context for these images: " + context.Trim();

        var content = new List<object> { new { type = "text", text } };
        foreach (var img in images)
        {
            using var ms = new MemoryStream();
            await img.Content.CopyToAsync(ms, ct);
            var dataUrl = $"data:{MimeFor(img.FileName)};base64,{Convert.ToBase64String(ms.ToArray())}";
            content.Add(new { type = "image_url", image_url = new { url = dataUrl } });
        }

        var payload = new
        {
            model = _model,
            messages = new object[] { new { role = "user", content } }
        };

        using var resp = await _http.PostAsJsonAsync("chat/completions", payload, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
