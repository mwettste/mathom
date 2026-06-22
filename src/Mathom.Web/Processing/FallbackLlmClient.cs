using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

public class FallbackLlmClient : ILlmClient
{
    private const int AttemptsPerProvider = 2;
    private readonly IReadOnlyList<ILlmClient> _providers;
    private readonly ILogger<FallbackLlmClient> _logger;
    private readonly TimeSpan _retryDelay;

    public FallbackLlmClient(IEnumerable<ILlmClient> providers, ILogger<FallbackLlmClient> logger, TimeSpan? retryDelay = null)
    {
        _providers = providers.ToList();
        _logger = logger;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
    }

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
                    _logger.LogWarning(ex, "Provider {Provider} attempt {Attempt} failed",
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
