using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Embeddings;

public class OpenRouterEmbeddingClient(HttpClient http, IConfiguration config)
    : OpenAiCompatibleEmbeddingClient(http, config, "Embeddings:OpenRouter", "https://openrouter.ai/api/v1/")
{
}
