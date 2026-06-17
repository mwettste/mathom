using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Xunit;

namespace Mathom.Tests;

public class FakeLlmClientTests
{
    [Fact]
    public async Task ReturnsDeterministicResult()
    {
        var fake = new FakeLlmClient();
        var result = await fake.CleanupAsync("  buy milk ", CancellationToken.None);
        Assert.Equal("buy milk", result.CleanText);
        Assert.Equal(ItemType.Note, result.ItemType);
        Assert.Single(result.Tags);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("Title: buy milk", result.Title);
        Assert.False(result.Actionable);
    }

    [Fact]
    public async Task ThrowFlag_ThrowsAndStillIncrementsCalls()
    {
        var fake = new FakeLlmClient { Throw = true };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.CleanupAsync("x", CancellationToken.None));
        Assert.Equal(1, fake.Calls);
    }
}
