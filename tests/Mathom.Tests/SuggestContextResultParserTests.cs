using System;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class SuggestContextResultParserTests
{
    private static readonly string[] Keys = { "c1", "c2", "c3" };

    [Fact]
    public void Parse_KnownKey_ReturnsKey()
        => Assert.Equal("c2", SuggestContextResultParser.Parse("""{"choice":"c2"}""", Keys));

    [Fact]
    public void Parse_None_ReturnsNull()
        => Assert.Null(SuggestContextResultParser.Parse("""{"choice":"none"}""", Keys));

    [Fact]
    public void Parse_UnknownKey_ReturnsNull()
        => Assert.Null(SuggestContextResultParser.Parse("""{"choice":"c9"}""", Keys));

    [Fact]
    public void Parse_ContextNameInsteadOfKey_ReturnsNull()
        => Assert.Null(SuggestContextResultParser.Parse("""{"choice":"Arbeit"}""", Keys));

    [Fact]
    public void Parse_MissingChoice_ReturnsNull()
        => Assert.Null(SuggestContextResultParser.Parse("""{"other":"c1"}""", Keys));

    [Fact]
    public void Parse_NotJson_ThrowsFormatException()
        => Assert.Throws<FormatException>(() => SuggestContextResultParser.Parse("not json", Keys));
}
