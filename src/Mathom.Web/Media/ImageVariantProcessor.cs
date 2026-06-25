using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Mathom.Web.Media;

/// <summary>
/// Pure image processing for photos: flatten any transparency onto white (JPEG has no alpha),
/// downscale to fit within <see cref="MaxEdge"/> preserving aspect, and re-encode as JPEG.
/// No storage or EF knowledge. Stateless — registered as a singleton.
/// </summary>
public sealed class ImageVariantProcessor
{
    private const int MaxEdge = 1600;

    /// <summary>Reads an image from <paramref name="source"/> and returns a rewound JPEG stream
    /// whose longest edge is ≤ 1600px.</summary>
    public async Task<Stream> CreateDisplayVariantAsync(Stream source, CancellationToken ct)
    {
        using var image = await Image.LoadAsync(source, ct);
        image.Mutate(ctx => ctx
            .BackgroundColor(Color.White)
            .Resize(new ResizeOptions
            {
                Size = new Size(MaxEdge, MaxEdge),
                Mode = ResizeMode.Max,          // fit within the box, preserve aspect ratio
                Sampler = KnownResamplers.Lanczos2,
            }));

        var output = new MemoryStream();
        await image.SaveAsync(output, new JpegEncoder(), ct);
        output.Position = 0;
        return output;
    }
}
