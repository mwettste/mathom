using System.Net.Http;
using System.Text.Json;
using System.Linq;
using Mathom.Web.Processing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Mathom.Tests;

public class InfomaniakSuggestFormatTests
{
    private sealed class Probe(HttpClient http, IConfiguration config) : InfomaniakLlmClient(http, config)
    {
        public object Format(System.Collections.Generic.IReadOnlyList<string> keys) => BuildSuggestResponseFormat(keys);
    }

    [Fact]
    public void SuggestResponseFormat_IsStrictJsonSchema_WithKeysPlusNone()
    {
        var probe = new Probe(new HttpClient(), new ConfigurationBuilder().Build());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(probe.Format(new[] { "c1", "c2" })));
        var root = doc.RootElement;
        Assert.Equal("json_schema", root.GetProperty("type").GetString());
        var js = root.GetProperty("json_schema");
        Assert.True(js.GetProperty("strict").GetBoolean());
        var en = js.GetProperty("schema").GetProperty("properties").GetProperty("choice").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "c1", "c2", "none" }, en);
    }
}
