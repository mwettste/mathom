using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

public class OpenRouterLlmClient(HttpClient http, IConfiguration config)
    : OpenAiCompatibleLlmClient(http, config, "Llm:OpenRouter", "https://openrouter.ai/api/v1/")
{
}
