using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

/// <summary>
/// Infomaniak Whisper transcription. The API is ASYNCHRONOUS: POSTing the audio to
/// <c>{base}/audio/transcriptions</c> returns a <c>batch_id</c>; the transcript is then polled
/// from <c>{product-root}/results/{batch_id}</c> (one level above the <c>/openai/</c> base) until
/// its <c>status</c> is <c>success</c>. The result's <c>data</c> field is a JSON string wrapping
/// <c>{"text": "..."}</c>. The configured <c>Stt:Infomaniak:BaseUrl</c> must end with a slash and
/// include the product-id + <c>/openai/</c> segment, e.g. <c>https://api.infomaniak.com/1/ai/&lt;id&gt;/openai/</c>.
/// </summary>
public class InfomaniakTranscriber : ITranscriber
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(180);

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

        // 1. Submit the audio — returns a batch id (transcription runs asynchronously).
        string batchId;
        using (var form = new MultipartFormDataContent())
        {
            var audioContent = new StreamContent(audio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(audioContent, "file", fileName);
            form.Add(new StringContent(_model), "model");

            using var submit = await _http.PostAsync("audio/transcriptions", form, ct);
            submit.EnsureSuccessStatusCode();
            using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync(ct));
            batchId = submitDoc.RootElement.TryGetProperty("batch_id", out var b) ? b.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(batchId))
                throw new FormatException("Transcription submit response had no 'batch_id'.");
        }

        // 2. Poll for completion. Results live at the product root: "../results/{id}" relative to
        //    the ".../{id}/openai/" base resolves to ".../{id}/results/{id}".
        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var poll = await _http.GetAsync($"../results/{batchId}", ct);
            poll.EnsureSuccessStatusCode();
            using var pollDoc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(ct));
            var root = pollDoc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (status == "success")
            {
                // `data` is a JSON string of the form {"text": "..."}.
                var dataStr = root.TryGetProperty("data", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(dataStr))
                    throw new FormatException("Transcription succeeded but 'data' was empty.");
                using var dataDoc = JsonDocument.Parse(dataStr);
                return dataDoc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            }

            if (status != "pending")
                throw new InvalidOperationException(
                    $"Transcription failed for batch {batchId} with status '{status}'.");

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Transcription did not complete within {PollTimeout.TotalSeconds:0}s (batch {batchId}).");

            await Task.Delay(PollInterval, ct);
        }
    }
}
