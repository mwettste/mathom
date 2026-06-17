using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Processing;

public interface ITranscriber
{
    Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken ct);
}
