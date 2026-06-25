using System;
using System.Collections.Generic;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class CleanupPromptBuilderTests
{
    [Fact]
    public void EmptyGlossary_LeavesPromptUnchanged()
    {
        var withEmpty = CleanupPromptBuilder.BuildSystemPrompt(Array.Empty<string>());
        Assert.DoesNotContain("glossary", withEmpty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyGlossary_IncludesTermsAndInstruction()
    {
        var p = CleanupPromptBuilder.BuildSystemPrompt(new List<string> { "Obersaxen", "Mathom" });
        Assert.Contains("Obersaxen", p);
        Assert.Contains("Mathom", p);
        Assert.Contains("glossary", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyGlossary_HasStrongerCorrectionInstruction()
    {
        var p = CleanupPromptBuilder.BuildSystemPrompt(new System.Collections.Generic.List<string> { "FireSkills (also heard as: Fairstills)" });
        Assert.Contains("FireSkills", p);
        Assert.Contains("also heard as: Fairstills", p);
        Assert.Contains("mis-transcribed", p, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Glossary_WithDescription_PassesItThrough_AndHasComprehensionInstruction()
    {
        var p = CleanupPromptBuilder.BuildSystemPrompt(new System.Collections.Generic.List<string> { "FireSkills — our internal time-tracking product" });
        Assert.Contains("FireSkills — our internal time-tracking product", p);
        Assert.Contains("em dash", p, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemPrompt_InstructsSameLanguage_AndAsksForLanguageField()
    {
        var p = CleanupPromptBuilder.BuildSystemPrompt(System.Array.Empty<string>());
        Assert.Contains("same language", p, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("language", p, System.StringComparison.OrdinalIgnoreCase);
    }
}
