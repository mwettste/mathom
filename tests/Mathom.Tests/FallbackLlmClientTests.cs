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

        var fallback = new FallbackLlmClient(new ILlmClient[] { primary, secondary }, NullLogger<FallbackLlmClient>.Instance);
        var result = await fallback.CleanupAsync("hi", CancellationToken.None);

        Assert.Equal("ok", result.CleanText);
        Assert.True(primary.Calls >= 1);
        Assert.True(secondary.Calls >= 1);
    }

    [Fact]
    public async Task Throws_WhenAllProvidersFail()
    {
        var fallback = new FallbackLlmClient(
            new ILlmClient[] { new FakeLlmClient { Throw = true }, new FakeLlmClient { Throw = true } },
            NullLogger<FallbackLlmClient>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => fallback.CleanupAsync("hi", CancellationToken.None));
    }
}
