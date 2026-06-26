using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

/// <summary>
/// Infomaniak chat completions. Unlike most OpenAI-compatible providers, Infomaniak rejects
/// <c>response_format: json_object</c> and requires <c>json_schema</c>, served from its v2
/// OpenAI endpoint (<c>.../2/ai/&lt;product-id&gt;/openai/v1/</c>).
/// </summary>
public class InfomaniakLlmClient(HttpClient http, IConfiguration config)
    : OpenAiCompatibleLlmClient(http, config, "Llm:Infomaniak", "https://api.infomaniak.com/2/ai/")
{
    protected override object BuildResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "cleanup",
            strict = true,
            schema = CleanupPromptBuilder.ResponseSchema(),
        },
    };

    protected override object BuildTranslateResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "translation",
            strict = true,
            schema = TranslatePromptBuilder.ResponseSchema(),
        },
    };
}
