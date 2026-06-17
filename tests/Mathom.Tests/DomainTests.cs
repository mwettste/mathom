using System;
using Mathom.Web.Domain;
using Xunit;

namespace Mathom.Tests;

public class DomainTests
{
    [Fact]
    public void CreatePending_SetsDefaults()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Item.CreatePending(SourceType.Text, "  raw note  ", "key-1", now);

        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal(ItemStatus.Pending, item.Status);
        Assert.Equal(SourceType.Text, item.SourceType);
        Assert.Equal("  raw note  ", item.RawText);
        Assert.Equal("key-1", item.IdempotencyKey);
        Assert.Equal(now, item.CreatedAt);
        Assert.Null(item.CleanText);
        Assert.Null(item.ProcessedAt);
        Assert.False(item.Actionable);
    }
}
