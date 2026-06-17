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

    public FallbackLlmClient(IEnumerable<ILlmClient> providers, ILogger<FallbackLlmClient> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public async Task<CleanupResult> CleanupAsync(string rawText, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var provider in _providers)
        {
            for (var attempt = 1; attempt <= AttemptsPerProvider; attempt++)
            {
                try
                {
                    return await provider.CleanupAsync(rawText, ct);
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "Provider {Provider} attempt {Attempt} failed",
                        provider.GetType().Name, attempt);
                }
            }
        }
        throw new InvalidOperationException("All LLM providers failed.", last);
    }
}
