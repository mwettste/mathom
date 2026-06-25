using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Processing;

namespace Mathom.Tests;

public class FakeImageReader : IImageReader
{
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public int LastImageCount { get; private set; }
    public Func<IReadOnlyList<ImageData>, string> Respond { get; set; } = imgs => $"read of {imgs.Count} image(s)";
    public IReadOnlyList<string> LastGlossary { get; private set; } = Array.Empty<string>();

    public Task<string> ExtractAsync(IReadOnlyList<ImageData> images, IReadOnlyList<string> glossary, CancellationToken ct)
    {
        Calls++;
        LastImageCount = images.Count;
        LastGlossary = glossary;
        if (Throw) throw new InvalidOperationException("fake image-read failure");
        return Task.FromResult(Respond(images));
    }
}
