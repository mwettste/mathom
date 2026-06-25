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

    public Task<CleanupResult> CleanupAsync(string rawText, IReadOnlyList<string> glossary, CancellationToken ct)
        => WithFallbackAsync("cleanup", (p, c) => p.CleanupAsync(rawText, glossary, c), ct);

    public Task<TranslationResult> TranslateAsync(
        string sourceTitle, string sourceText, string targetLocale, string styleHint,
        IReadOnlyList<string> glossaryTerms, CancellationToken ct)
        => WithFallbackAsync("translate", (p, c) => p.TranslateAsync(sourceTitle, sourceText, targetLocale, styleHint, glossaryTerms, c), ct);

    // Try each provider in order, AttemptsPerProvider times each, delaying between
    // attempts. Shared by every ILlmClient operation so the retry policy lives in one place.
    private async Task<T> WithFallbackAsync<T>(string op, Func<ILlmClient, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        Exception? last = null;
        for (var providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
        {
            var provider = _providers[providerIndex];
            for (var attempt = 1; attempt <= AttemptsPerProvider; attempt++)
            {
                try
                {
                    return await call(provider, ct);
                }
                catch (Exception ex)
                {
                    last = ex;
                    logger.LogWarning(ex, "Provider {Provider} {Op} attempt {Attempt} failed",
                        provider.GetType().Name, op, attempt);
                    bool isLastAttemptForProvider = attempt == AttemptsPerProvider;
                    bool isLastProvider = providerIndex == _providers.Count - 1;
                    if (!isLastAttemptForProvider || !isLastProvider)
                        await Task.Delay(_retryDelay * attempt, ct);
                }
            }
        }
        throw new InvalidOperationException($"All LLM providers failed ({op}).", last);
    }
}
