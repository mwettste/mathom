using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Media;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Mathom.Tests;

public class LocalDiskMediaStoreTests
{
    private static IConfiguration ConfigWithRoot(string root) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["Media:Root"] = root })
            .Build();

    [Fact]
    public async Task SaveThenOpenRead_RoundTripsBytes_AndPreservesExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "mathom-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalDiskMediaStore(ConfigWithRoot(root));
            var payload = new byte[] { 10, 20, 30, 40 };

            string key;
            using (var input = new MemoryStream(payload))
                key = await store.SaveAsync(input, ".webm", CancellationToken.None);

            Assert.EndsWith(".webm", key);

            await using var read = await store.OpenReadAsync(key, CancellationToken.None);
            using var outMs = new MemoryStream();
            await read.CopyToAsync(outMs);
            Assert.Equal(payload, outMs.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
