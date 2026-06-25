using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mathom.Tests;

public class ImageVariantProcessorTests
{
    private static async Task<Stream> MakePngAsync(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        var ms = new MemoryStream();
        await img.SaveAsync(ms, new PngEncoder());
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task CreateDisplayVariant_CapsLongestEdgeTo1600_AndOutputsJpeg()
    {
        var source = await MakePngAsync(3000, 2000);
        var processor = new ImageVariantProcessor();

        await using var variant = await processor.CreateDisplayVariantAsync(source, CancellationToken.None);

        var format = await Image.DetectFormatAsync(variant);
        Assert.Equal("JPEG", format.Name);
        variant.Position = 0;
        var info = await Image.IdentifyAsync(variant);
        Assert.True(System.Math.Max(info.Width, info.Height) <= 1600);
        Assert.Equal(1600, info.Width); // 3000x2000 scales to 1600x1066
    }

    [Fact]
    public async Task CreateDisplayVariant_DoesNotEnlargeBeyond1600_ForSmallImages()
    {
        var source = await MakePngAsync(800, 600);
        var processor = new ImageVariantProcessor();

        await using var variant = await processor.CreateDisplayVariantAsync(source, CancellationToken.None);

        variant.Position = 0;
        var info = await Image.IdentifyAsync(variant);
        Assert.True(System.Math.Max(info.Width, info.Height) <= 1600);
    }
}
