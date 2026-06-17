using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

public class InfomaniakTranscriber : ITranscriber
{
    private readonly HttpClient _http;
    private readonly string _model;

    public InfomaniakTranscriber(HttpClient http, IConfiguration config)
    {
        _http = http;
        var section = config.GetSection("Stt:Infomaniak");
        _model = section["Model"] ?? string.Empty;
        _http.BaseAddress ??= new Uri(section["BaseUrl"] ?? "https://api.infomaniak.com/1/ai/");
        var key = section["ApiKey"];
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException(
                "Speech-to-text model is not configured for 'Stt:Infomaniak'. Set Stt:Infomaniak:Model and " +
                "Stt:Infomaniak:ApiKey via configuration — e.g. appsettings.Development.json, or the " +
                "Stt__Infomaniak__Model environment variable.");

        using var form = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(audioContent, "file", fileName);
        form.Add(new StringContent(_model), "model");

        using var resp = await _http.PostAsync("audio/transcriptions", form, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("text").GetString()
            ?? throw new FormatException("Transcription response had no 'text' field.");
    }
}
