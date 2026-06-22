using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Media;

namespace Mathom.Tests;

public class FakeMediaStore : IMediaStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public async Task<string> SaveAsync(Stream content, string fileExtension, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var ext = string.IsNullOrWhiteSpace(fileExtension) ? "" : (fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension);
        var key = Guid.NewGuid().ToString("N") + ext;
        _blobs[key] = ms.ToArray();
        return key;
    }

    public Task<Stream> OpenReadAsync(string mediaPath, CancellationToken ct)
        => Task.FromResult<Stream>(new MemoryStream(_blobs[mediaPath]));

    public Task DeleteAsync(string mediaPath, CancellationToken ct)
    {
        _blobs.TryRemove(mediaPath, out _);
        return Task.CompletedTask;
    }

    public bool Has(string mediaPath) => _blobs.ContainsKey(mediaPath);
}
