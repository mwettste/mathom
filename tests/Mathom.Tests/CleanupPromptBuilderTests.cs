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
}
