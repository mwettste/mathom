using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mathom.Tests;

public class FakeTranscriberTests
{
    [Fact]
    public async Task ReturnsTranscriptAndCountsCalls()
    {
        var fake = new FakeTranscriber { Respond = _ => "hello world" };
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        var text = await fake.TranscribeAsync(ms, "note.webm", CancellationToken.None);
        Assert.Equal("hello world", text);
        Assert.Equal(1, fake.Calls);
    }
}
