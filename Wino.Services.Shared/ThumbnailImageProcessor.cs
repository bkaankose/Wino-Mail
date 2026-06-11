#nullable enable
using System;
using SkiaSharp;

namespace Wino.Services;

public static class ThumbnailImageProcessor
{
    public const int AvatarCachePixelSize = 48;
    private const int JpegQuality = 78;

    public static NormalizedThumbnail? NormalizeAvatar(byte[] imageData, int pixelSize = AvatarCachePixelSize)
    {
        if (imageData == null || imageData.Length == 0 || pixelSize <= 0)
            return null;

        using var sourceBitmap = SKBitmap.Decode(imageData);
        if (sourceBitmap == null || sourceBitmap.Width == 0 || sourceBitmap.Height == 0)
            return null;

        var cropSize = Math.Min(sourceBitmap.Width, sourceBitmap.Height);
        var sourceRect = SKRectI.Create(
            (sourceBitmap.Width - cropSize) / 2,
            (sourceBitmap.Height - cropSize) / 2,
            cropSize,
            cropSize);

        using var outputBitmap = new SKBitmap(new SKImageInfo(pixelSize, pixelSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(outputBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(sourceBitmap, sourceRect, new SKRect(0, 0, pixelSize, pixelSize));
        }

        using var outputImage = SKImage.FromBitmap(outputBitmap);
        var hasTransparency = HasTransparentPixels(outputBitmap);
        var format = hasTransparency ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var encodedData = outputImage.Encode(format, JpegQuality);

        return encodedData == null
            ? null
            : new NormalizedThumbnail(encodedData.ToArray(), hasTransparency ? ".png" : ".jpg");
    }

    private static bool HasTransparentPixels(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < byte.MaxValue)
                    return true;
            }
        }

        return false;
    }
}

public sealed record NormalizedThumbnail(byte[] Data, string FileExtension);
