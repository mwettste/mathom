using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Processing;
using Xunit;

namespace Mathom.Tests;

public class FakeImageReaderTests
{
    private static ImageData Img() => new(new MemoryStream(new byte[] { 1 }), "a.jpg");

    [Fact]
    public async Task Records_Glossary_And_ImageCount()
    {
        var r = new FakeImageReader();
        var text = await r.ExtractAsync(new[] { Img(), Img() }, new List<string> { "Obersaxen" }, "my context", CancellationToken.None);
        Assert.Equal("read of 2 image(s)", text);
        Assert.Equal(1, r.Calls);
        Assert.Equal(2, r.LastImageCount);
        Assert.Contains("Obersaxen", r.LastGlossary);
        Assert.Equal("my context", r.LastContext);
    }

    [Fact]
    public async Task Throws_When_Configured()
    {
        var r = new FakeImageReader { Throw = true };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => r.ExtractAsync(new[] { Img() }, Array.Empty<string>(), null, CancellationToken.None));
    }
}
