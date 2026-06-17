using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mathom.Web.Processing;

public class NotConfiguredLlmClient : ILlmClient
{
    public Task<CleanupResult> CleanupAsync(string rawText, CancellationToken ct)
        => throw new NotImplementedException("LLM provider is configured in Task 9.");
}
