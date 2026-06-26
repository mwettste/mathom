using System.Collections.Generic;
using Mathom.Web.Languages;
using Xunit;

namespace Mathom.Tests;

public class LocalesTests
{
    [Fact]
    public void DeDe_And_DeCh_AreDistinct_ShareBaseGerman()
    {
        Assert.True(Locales.IsSupported("de-DE"));
        Assert.True(Locales.IsSupported("de-CH"));
        Assert.Equal("de", Locales.Find("de-DE")!.BaseLanguage);
        Assert.Equal("de", Locales.Find("de-CH")!.BaseLanguage);
        Assert.NotEqual(Locales.Find("de-DE")!.StyleHint, Locales.Find("de-CH")!.StyleHint);
    }

    [Fact]
    public void Resolve_PrefersPrimary_WhenBaseMatches()
        => Assert.Equal("de-CH",
            Locales.ResolveSourceLocale("de", primaryLocale: "de-CH", activeLocales: new[] { "de-CH", "de-DE", "en" }));

    [Fact]
    public void Resolve_FallsBackToActiveLocale_OfThatBase_WhenPrimaryDiffers()
        => Assert.Equal("de-DE",
            Locales.ResolveSourceLocale("de", primaryLocale: "en", activeLocales: new[] { "en", "de-DE" }));

    [Fact]
    public void Resolve_FallsBackToCatalogDefault_WhenBaseNotActive()
        => Assert.Equal("fr-FR",
            Locales.ResolveSourceLocale("fr", primaryLocale: "en", activeLocales: new[] { "en" }));

    [Fact]
    public void Resolve_UsesPrimary_WhenDetectionMissingOrUnknown()
    {
        Assert.Equal("en", Locales.ResolveSourceLocale(null, "en", new List<string>()));
        Assert.Equal("en", Locales.ResolveSourceLocale("zz", "en", new[] { "en" }));
    }

    [Fact]
    public void Resolve_NormalizesCaseAndRegionInput()
        => Assert.Equal("de-DE", Locales.ResolveSourceLocale("DE-de", "en", new[] { "en", "de-DE" }));
}
