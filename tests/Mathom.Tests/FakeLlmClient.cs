using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Processing;

namespace Mathom.Tests;

public class FakeLlmClient : ILlmClient
{
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public Func<string, CleanupResult> Respond { get; set; } = raw =>
        new CleanupResult(
            Title: "Title: " + raw.Trim(),
            CleanText: raw.Trim(),
            ItemType: ItemType.Note,
            Actionable: false,
            Tags: new List<CleanupTag> { new("general", TagKind.Topic) });

    public IReadOnlyList<string> LastGlossary { get; private set; } = System.Array.Empty<string>();

    public Task<CleanupResult> CleanupAsync(string rawText, IReadOnlyList<string> glossary, CancellationToken ct)
    {
        Calls++;
        LastGlossary = glossary;
        if (Throw) throw new InvalidOperationException("fake failure");
        return Task.FromResult(Respond(rawText));
    }
}
