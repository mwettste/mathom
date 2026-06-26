using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Embeddings;

namespace Mathom.Tests;

// Deterministic embeddings for tests. By default derives a stable vector from the text so
// equal text → equal vector. Tests that need controlled geometry set Embed explicitly.
public class FakeEmbeddingClient : IEmbeddingClient
{
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public string ModelId { get; set; } = "fake-embed-v1";

    public Func<string, float[]> Embed { get; set; } = text =>
    {
        var v = new float[EmbeddingConfig.Dimensions];
        var seed = (uint)text.GetHashCode();
        for (var i = 0; i < v.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            v[i] = (seed % 1000) / 1000f;
        }
        return v;
    };

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("fake embed failure");
        return Task.FromResult(Embed(text));
    }
}
