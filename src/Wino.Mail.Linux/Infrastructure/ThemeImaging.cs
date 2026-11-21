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
    /// <summary>Decodes the file scaled to fit inside the box (aspect kept, never enlarged); null when it is not an image.</summary>
    public static ScaledImage? DecodeToFit(string path, int maxWidth, int maxHeight)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            return codec is null ? null : Decode(codec, maxWidth, maxHeight);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Main thread.</summary>
    public static Gdk.Texture ToTexture(ScaledImage image)
    {
        using var bytes = GLib.Bytes.New(image.Pixels);
        return Gdk.MemoryTexture.New(image.Width, image.Height, Gdk.MemoryFormat.R8g8b8a8Premultiplied, bytes, (nuint)image.Stride);
    }

    private static ScaledImage? Decode(SKCodec codec, int maxWidth, int maxHeight)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        var scale = Math.Min(1f, Math.Min((float)maxWidth / info.Width, (float)maxHeight / info.Height));
        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));

        // JPEG decodes at 1/2, 1/4 or 1/8 for free; the rest of the way is a filtered resize.
        var native = codec.GetScaledDimensions(scale);
        if (native.Width < width || native.Height < height) native = new SKSizeI(info.Width, info.Height);
        using var decoded = new SKBitmap(new SKImageInfo(native.Width, native.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        if (decoded.Width == width && decoded.Height == height) return Copy(decoded);
        using var scaled = decoded.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return scaled is null ? null : Copy(scaled);
    }

    private static ScaledImage Copy(SKBitmap bitmap) => new(bitmap.Bytes, bitmap.Width, bitmap.Height, bitmap.RowBytes);
}
