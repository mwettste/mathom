using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mathom.Tests;

public class FallbackLlmClientTests
{
    [Fact]
    public async Task UsesSecondProvider_WhenFirstAlwaysFails()
    {
        var primary = new FakeLlmClient { Throw = true };
        var secondary = new FakeLlmClient
        {
            Respond = _ => new CleanupResult("ok", "ok", ItemType.Note, false, Array.Empty<CleanupTag>())
        };

        var fallback = new FallbackLlmClient(new ILlmClient[] { primary, secondary }, NullLogger<FallbackLlmClient>.Instance, retryDelay: TimeSpan.Zero);
        var result = await fallback.CleanupAsync("hi", System.Array.Empty<string>(), CancellationToken.None);

        Assert.Equal("ok", result.CleanText);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, secondary.Calls);
    }

    [Fact]
    public async Task Throws_WhenAllProvidersFail()
    {
        var fallback = new FallbackLlmClient(
            new ILlmClient[] { new FakeLlmClient { Throw = true }, new FakeLlmClient { Throw = true } },
            NullLogger<FallbackLlmClient>.Instance,
            retryDelay: TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<Exception>(() => fallback.CleanupAsync("hi", System.Array.Empty<string>(), CancellationToken.None));
    }

    [Fact]
    public async Task Translate_FallsToSecondProvider_OnFirstFailure()
    {
        var good = new Mathom.Tests.FakeLlmClient
        {
            TranslateRespond = (_, text, locale) => new Mathom.Web.Processing.TranslationResult("T-" + locale, text),
        };
        var bad = new Mathom.Tests.FakeLlmClient { ThrowTranslate = true };
        var fallback = new Mathom.Web.Processing.FallbackLlmClient(
            new Mathom.Web.Processing.ILlmClient[] { bad, good },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mathom.Web.Processing.FallbackLlmClient>.Instance,
            System.TimeSpan.Zero);

        var r = await fallback.TranslateAsync("Title", "Body", "en", "", System.Array.Empty<string>(), System.Threading.CancellationToken.None);
        Assert.Equal("T-en", r.Title);
    }
}
