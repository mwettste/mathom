using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Embeddings;

// Mirrors FallbackLlmClient: try each provider AttemptsPerProvider times, in order.
public class FallbackEmbeddingClient(
    IEnumerable<IEmbeddingClient> providers,
    ILogger<FallbackEmbeddingClient> logger,
    TimeSpan? retryDelay = null) : IEmbeddingClient
{
    private const int AttemptsPerProvider = 2;
    private readonly IReadOnlyList<IEmbeddingClient> _providers = providers.ToList();
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);

    // The primary provider defines the stored model id (the vector space notes are embedded into).
    public string ModelId => _providers[0].ModelId;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        Exception? last = null;
        for (var p = 0; p < _providers.Count; p++)
        {
            for (var attempt = 1; attempt <= AttemptsPerProvider; attempt++)
            {
                try
                {
                    return await _providers[p].EmbedAsync(text, ct);
                }
                catch (Exception ex)
                {
                    last = ex;
                    logger.LogWarning(ex, "Embedding provider {Provider} attempt {Attempt} failed",
                        _providers[p].GetType().Name, attempt);
                    var isLastAttempt = attempt == AttemptsPerProvider;
                    var isLastProvider = p == _providers.Count - 1;
                    if (!isLastAttempt || !isLastProvider)
                        await Task.Delay(_retryDelay * attempt, ct);
                }
            }
        }
        throw new InvalidOperationException("All embedding providers failed.", last);
    }
}
