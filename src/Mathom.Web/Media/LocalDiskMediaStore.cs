using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Media;

public class LocalDiskMediaStore : IMediaStore
{
    private readonly string _root;

    public LocalDiskMediaStore(IConfiguration config)
    {
        _root = config["Media:Root"] ?? "media";
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string fileExtension, CancellationToken ct)
    {
        var ext = string.IsNullOrWhiteSpace(fileExtension) ? "" : fileExtension;
        if (ext.Length > 0 && !ext.StartsWith('.')) ext = "." + ext;
        var key = Guid.NewGuid().ToString("N") + ext;
        var path = Path.Combine(_root, key);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream> OpenReadAsync(string mediaPath, CancellationToken ct)
    {
        var path = ResolveWithinRoot(mediaPath);
        Stream stream = File.OpenRead(path);
        return Task.FromResult(stream);
    }

    private string ResolveWithinRoot(string mediaPath)
    {
        var rootFull = Path.GetFullPath(_root);
        var full = Path.GetFullPath(Path.Combine(rootFull, mediaPath));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Media key escapes the storage root.", nameof(mediaPath));
        return full;
    }
}
