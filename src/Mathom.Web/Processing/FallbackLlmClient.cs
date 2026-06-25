using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

public class FallbackLlmClient(IEnumerable<ILlmClient> providers, ILogger<FallbackLlmClient> logger, TimeSpan? retryDelay = null) : ILlmClient
{
    private const int AttemptsPerProvider = 2;
    private readonly IReadOnlyList<ILlmClient> _providers = providers.ToList();
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);

    public async Task<CleanupResult> CleanupAsync(string rawText, IReadOnlyList<string> glossary, CancellationToken ct)
    {
        Exception? last = null;
        for (var providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
        {
            var provider = _providers[providerIndex];
            for (var attempt = 1; attempt <= AttemptsPerProvider; attempt++)
            {
                try
                {
                    return await provider.CleanupAsync(rawText, glossary, ct);
                }
                catch (Exception ex)
                {
                    last = ex;
                    logger.LogWarning(ex, "Provider {Provider} attempt {Attempt} failed",
                        provider.GetType().Name, attempt);

                    // Delay only when there is another attempt remaining (either for this provider or another provider after it).
                    bool isLastAttemptForProvider = attempt == AttemptsPerProvider;
                    bool isLastProvider = providerIndex == _providers.Count - 1;
                    if (!isLastAttemptForProvider || !isLastProvider)
                    {
                        await Task.Delay(_retryDelay * attempt, ct);
                    }
                }
            }
        }
        throw new InvalidOperationException("All LLM providers failed.", last);
    }
}
