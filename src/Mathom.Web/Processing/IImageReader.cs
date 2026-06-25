using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Processing;

/// <summary>One image to read: its content stream and a file name whose extension
/// determines the MIME type sent to the vision model.</summary>
public readonly record struct ImageData(Stream Content, string FileName);

/// <summary>Reads the text (and a brief description of non-text content) out of one or more
/// images into a single string. Parallel to <see cref="ITranscriber"/> for the voice path.</summary>
public interface IImageReader
{
    Task<string> ExtractAsync(IReadOnlyList<ImageData> images, IReadOnlyList<string> glossary, string? context, CancellationToken ct);
}
