using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Processing;

public class InfomaniakLlmClient : OpenAiCompatibleLlmClient
{
    public InfomaniakLlmClient(HttpClient http, IConfiguration config)
        : base(http, config, "Llm:Infomaniak", "https://api.infomaniak.com/1/ai/") { }
}
