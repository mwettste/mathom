using System;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class CleanupResultParsingTests
{
    [Fact]
    public void Parse_ValidJson_MapsAllFields()
    {
        var json = """
        {"title":"Buy milk","clean_text":"Remember to buy milk.","item_type":"task","actionable":true,
         "tags":[{"name":"errands","kind":"topic"},{"name":"Alice","kind":"person"}]}
        """;
        var result = CleanupResultParser.Parse(json);
        Assert.Equal("Buy milk", result.Title);
        Assert.Equal(ItemType.Task, result.ItemType);
        Assert.True(result.Actionable);
        Assert.Equal(2, result.Tags.Count);
        Assert.Equal(TagKind.Person, result.Tags[1].Kind);
    }

    [Fact]
    public void Parse_MissingRequiredField_Throws()
    {
        var json = """{"clean_text":"x","item_type":"note","actionable":false,"tags":[]}""";
        Assert.Throws<FormatException>(() => CleanupResultParser.Parse(json));
    }

    [Fact]
    public void Parse_UnknownEnum_Throws()
    {
        var json = """{"title":"t","clean_text":"x","item_type":"banana","actionable":false,"tags":[]}""";
        Assert.Throws<FormatException>(() => CleanupResultParser.Parse(json));
    }
}
