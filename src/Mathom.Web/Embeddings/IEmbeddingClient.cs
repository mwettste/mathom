using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Embeddings;

public interface IEmbeddingClient
{
    /// <summary>Identifier of the active model, stored on the note so the backfill can detect staleness.</summary>
    string ModelId { get; }

    /// <summary>Returns the embedding vector for <paramref name="text"/>. Length == EmbeddingConfig.Dimensions.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
