using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Processing;

namespace Mathom.Tests;

public class FakeTranscriber : ITranscriber
{
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public Func<string, string> Respond { get; set; } = fileName => "transcript of " + fileName;

    public System.Collections.Generic.IReadOnlyList<string> LastGlossary { get; private set; }
        = System.Array.Empty<string>();

    public Task<string> TranscribeAsync(Stream audio, string fileName, System.Collections.Generic.IReadOnlyList<string> glossary, CancellationToken ct)
    {
        Calls++;
        LastGlossary = glossary;
        if (Throw) throw new InvalidOperationException("fake transcription failure");
        return Task.FromResult(Respond(fileName));
    }
}
