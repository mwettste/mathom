using System;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class TranslatePromptBuilderTests
{
    [Fact]
    public void SystemPrompt_NamesTargetLanguage_StyleHint_AndKeepsTerms()
    {
        var p = TranslatePromptBuilder.BuildSystemPrompt("de-CH", "Swiss Standard German: use 'ss'.", new[] { "FireSkills" });
        Assert.Contains("German (Switzerland)", p);   // resolved display name
        Assert.Contains("Swiss Standard German", p);   // style hint
        Assert.Contains("FireSkills", p);              // keep-as-is term
    }

    [Fact]
    public void Parser_ReadsTitleAndCleanText()
    {
        var r = TranslationResultParser.Parse("""{"title":"T","clean_text":"B"}""");
        Assert.Equal("T", r.Title);
        Assert.Equal("B", r.CleanText);
    }

    [Fact]
    public void Parser_Throws_OnMissingField()
        => Assert.Throws<FormatException>(() => TranslationResultParser.Parse("""{"title":"only"}"""));
}
