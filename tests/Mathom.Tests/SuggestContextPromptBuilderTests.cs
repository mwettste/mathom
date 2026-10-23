using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class SuggestContextPromptBuilderTests
{
    private static ContextCandidate Cand(string key, string name, string[]? terms = null, string[]? titles = null)
        => new(key, name, terms ?? System.Array.Empty<string>(), titles ?? System.Array.Empty<string>());

    [Fact]
    public void UserPrompt_ContainsKeysNamesTermsAndTitles_AsJson()
    {
        var json = SuggestContextPromptBuilder.BuildUserPrompt("Titel", "Text", new[]
        {
            Cand("c1", "Arbeit", new[] { "FireSkills" }, new[] { "Standup Notizen" }),
            Cand("c2", "Garten"),
        });

        using var doc = JsonDocument.Parse(json);
        var note = doc.RootElement.GetProperty("note");
        Assert.Equal("Titel", note.GetProperty("title").GetString());
        Assert.Equal("Text", note.GetProperty("text").GetString());
        var contexts = doc.RootElement.GetProperty("contexts");
        Assert.Equal(2, contexts.GetArrayLength());
        var c1 = contexts[0];
        Assert.Equal("c1", c1.GetProperty("key").GetString());
        Assert.Equal("Arbeit", c1.GetProperty("name").GetString());
        Assert.Equal("FireSkills", c1.GetProperty("glossary_terms")[0].GetString());
        Assert.Equal("Standup Notizen", c1.GetProperty("recent_titles")[0].GetString());
        Assert.Equal("c2", contexts[1].GetProperty("key").GetString());
    }

    [Fact]
    public void UserPrompt_TruncatesLongTitles_To120_AndText_To4000()
    {
        var json = SuggestContextPromptBuilder.BuildUserPrompt(
            new string('t', 500), new string('x', 9000),
            new[] { Cand("c1", "A", titles: new[] { new string('r', 300) }) });

        using var doc = JsonDocument.Parse(json);
        var note = doc.RootElement.GetProperty("note");
        Assert.Equal(120, note.GetProperty("title").GetString()!.Length);
        Assert.Equal(4000, note.GetProperty("text").GetString()!.Length);
        Assert.Equal(120, doc.RootElement.GetProperty("contexts")[0].GetProperty("recent_titles")[0].GetString()!.Length);
    }

    [Fact]
    public void SystemPrompt_MentionsNone_AndJsonOnly()
    {
        var p = SuggestContextPromptBuilder.BuildSystemPrompt();
        Assert.Contains("none", p);
        Assert.Contains("JSON", p);
    }

    [Fact]
    public void ResponseSchema_EnumIsKeysPlusNone()
    {
        var schema = SuggestContextPromptBuilder.ResponseSchema(new[] { "c1", "c2" });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
        var root = doc.RootElement;
        var choice = root.GetProperty("properties").GetProperty("choice");
        Assert.Equal("string", choice.GetProperty("type").GetString());
        Assert.Equal(new[] { "c1", "c2", "none" }, choice.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("choice", root.GetProperty("required")[0].GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }
}
