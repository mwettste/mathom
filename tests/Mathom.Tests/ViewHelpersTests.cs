using System;
using Mathom.Web.Pages;
using Xunit;

namespace Mathom.Tests;

public class CallNumberTests
{
    [Fact]
    public void For_uses_first_six_hex_uppercased_with_prefix()
    {
        var id = Guid.Parse("7f3a21bc-0000-0000-0000-000000000000");
        Assert.Equal("M·7F3A21", CallNumber.For(id));
    }

    [Fact]
    public void For_is_stable_for_same_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal(CallNumber.For(id), CallNumber.For(id));
    }
}
