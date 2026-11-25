using SkiaSharp;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Pixels of an image scaled down to fit a box, premultiplied RGBA, ready for a <c>Gdk.MemoryTexture</c>.</summary>
internal sealed record ScaledImage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>
/// Image work for themes with SkiaSharp, callable from any thread: no Gir.Core object is created here, so wallpapers are
/// decoded off the main thread and only the texture is made on it (<see cref="ToTexture"/>).
/// </summary>
internal static class ThemeImaging
{
    public const int PreviewWidth = 480;
    private const int PreviewQuality = 82;

    /// <summary>Decodes the file scaled to fit inside the box (aspect kept, never enlarged); null when it is not an image.</summary>
    public static ScaledImage? DecodeToFit(string path, int maxWidth, int maxHeight)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            using var bitmap = codec is null ? null : Decode(codec, maxWidth, maxHeight);
            return bitmap is null ? null : new ScaledImage(bitmap.Bytes, bitmap.Width, bitmap.Height, bitmap.RowBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The gallery preview of a custom theme's wallpaper, 480 px wide (the Android and macOS size); null when the data is not an image.</summary>
    public static byte[]? CreatePreviewJpeg(byte[] data)
    {
        try
        {
            using var stream = new SKMemoryStream(data);
            using var codec = SKCodec.Create(stream);
            using var bitmap = codec is null ? null : Decode(codec, PreviewWidth, int.MaxValue);
            using var encoded = bitmap?.Encode(SKEncodedImageFormat.Jpeg, PreviewQuality);
            return encoded?.ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The average colour of an image file as "#RRGGBB" (the editor's contrast check over a wallpaper); empty when it cannot be decoded.</summary>
    public static string AverageColorHex(string path)
    {
        var image = DecodeToFit(path, 16, 16);
        if (image is null) return string.Empty;
        long r = 0, g = 0, b = 0, weight = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = y * image.Stride + x * 4;
                // Premultiplied channels summed with alpha as the weight give the straight-alpha average.
                r += image.Pixels[i];
                g += image.Pixels[i + 1];
                b += image.Pixels[i + 2];
                weight += image.Pixels[i + 3];
            }
        }
        if (weight == 0) return string.Empty;
        static int Channel(long sum, long weight) => (int)Math.Clamp(sum * 255 / weight, 0, 255);
        return $"#{Channel(r, weight):X2}{Channel(g, weight):X2}{Channel(b, weight):X2}";
    }

    /// <summary>Main thread.</summary>
    public static Gdk.Texture ToTexture(ScaledImage image)
    {
        using var bytes = GLib.Bytes.New(image.Pixels);
        return Gdk.MemoryTexture.New(image.Width, image.Height, Gdk.MemoryFormat.R8g8b8a8Premultiplied, bytes, (nuint)image.Stride);
    }

    private static SKBitmap? Decode(SKCodec codec, int maxWidth, int maxHeight)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        var scale = Math.Min(1f, Math.Min((float)maxWidth / info.Width, (float)maxHeight / info.Height));
        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));

        // JPEG decodes at 1/2, 1/4 or 1/8 for free; the rest of the way is a filtered resize.
        var native = codec.GetScaledDimensions(scale);
        if (native.Width < width || native.Height < height) native = new SKSizeI(info.Width, info.Height);
        var decoded = new SKBitmap(new SKImageInfo(native.Width, native.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            return null;
        }
        if (decoded.Width == width && decoded.Height == height) return decoded;
        using (decoded)
        {
            return decoded.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
    }
}
