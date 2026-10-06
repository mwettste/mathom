using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Embeddings;

// Infomaniak's OpenAI-compatible embeddings endpoint. The real BaseUrl (with product id,
// e.g. https://api.infomaniak.com/2/ai/<product>/openai/v1/) is supplied via configuration/.env.
public class InfomaniakEmbeddingClient(HttpClient http, IConfiguration config)
    : OpenAiCompatibleEmbeddingClient(http, config, "Embeddings:Infomaniak", "https://api.infomaniak.com/2/ai/")
{
}
