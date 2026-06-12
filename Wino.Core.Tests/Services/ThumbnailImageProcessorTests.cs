using FluentAssertions;
using SkiaSharp;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class ThumbnailImageProcessorTests
{
    [Fact]
    public void NormalizeAvatar_ResizesAndCompressesOpaqueImage()
    {
        var input = CreateImageBytes(96, 64, SKColors.Red);

        var result = ThumbnailImageProcessor.NormalizeAvatar(input);

        result.Should().NotBeNull();
        result!.FileExtension.Should().Be(".jpg");

        using var outputBitmap = SKBitmap.Decode(result.Data);
        outputBitmap.Width.Should().Be(ThumbnailImageProcessor.AvatarCachePixelSize);
        outputBitmap.Height.Should().Be(ThumbnailImageProcessor.AvatarCachePixelSize);
    }

    [Fact]
    public void NormalizeAvatar_PreservesTransparencyAsPng()
    {
        var input = CreateImageBytes(96, 64, SKColors.Transparent);

        var result = ThumbnailImageProcessor.NormalizeAvatar(input);

        result.Should().NotBeNull();
        result!.FileExtension.Should().Be(".png");
    }

    private static byte[] CreateImageBytes(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
