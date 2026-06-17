using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Media;

public interface IMediaStore
{
    /// <summary>Stores the content and returns an opaque key (filename incl. extension).</summary>
    Task<string> SaveAsync(Stream content, string fileExtension, CancellationToken ct);

    /// <summary>Reopens previously-stored content by its key.</summary>
    Task<Stream> OpenReadAsync(string mediaPath, CancellationToken ct);
}
