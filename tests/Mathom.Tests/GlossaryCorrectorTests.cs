using System.Collections.Generic;
using System.Linq;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class GlossaryCorrectorTests
{
    private static CleanupResult Result(string title, string body, params string[] tagNames)
        => new(title, body, ItemType.Note, false, tagNames.Select(n => new CleanupTag(n, TagKind.Topic)).ToList());

    [Fact]
    public void Replaces_WholeWord_CaseInsensitive_InTitleBodyTags()
    {
        var map = new Dictionary<string, string> { ["Fairstills"] = "FireSkills" };
        var r = GlossaryCorrector.Apply(Result("Meeting Fairstills", "We met fairstills today.", "Fairstills"), map);

        Assert.Equal("Meeting FireSkills", r.Title);
        Assert.Equal("We met FireSkills today.", r.CleanText);
        Assert.Equal("FireSkills", r.Tags.Single().Name);
    }

    [Fact]
    public void DoesNotTouch_PartialMatches()
    {
        var map = new Dictionary<string, string> { ["Fairstills"] = "FireSkills" };
        var r = GlossaryCorrector.Apply(Result("Fairstillsy code", "x", "t"), map);
        Assert.Equal("Fairstillsy code", r.Title); // partial word untouched
    }

    [Fact]
    public void EmptyMap_IsNoOp()
    {
        var original = Result("a", "b", "c");
        var r = GlossaryCorrector.Apply(original, new Dictionary<string, string>());
        Assert.Equal("a", r.Title);
        Assert.Equal("b", r.CleanText);
    }
}
