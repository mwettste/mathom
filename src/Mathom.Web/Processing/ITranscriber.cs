using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Processing;

public interface ITranscriber
{
    Task<string> TranscribeAsync(Stream audio, string fileName, IReadOnlyList<string> glossary, CancellationToken ct);
}
