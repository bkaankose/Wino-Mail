using AppKit;
using CoreGraphics;
using Foundation;
using ImageIO;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Image work for custom themes. Everything here uses ImageIO and CoreGraphics only, which are safe
/// off the UI thread, so callers decode and scale wallpapers in the background. Wallpapers are kept
/// as the bytes the user picked (Windows writes a PNG into {id}.jpg too); ImageIO sniffs the content.
/// </summary>
internal static class MacThemeImaging
{
    /// <summary>True when the bytes decode as an image.</summary>
    public static bool IsImage(byte[]? data)
    {
        if (data is not { Length: > 0 }) return false;
        using var source = CGImageSource.FromData(NSData.FromArray(data));
        if (source is null || source.ImageCount == 0) return false;
        using var image = source.CreateImage(0, (CGImageOptions?)null);
        return image is not null;
    }

    /// <summary>Decodes a wallpaper, or null when the bytes are not an image.</summary>
    public static NSImage? Decode(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        using var source = CGImageSource.FromData(NSData.FromArray(data));
        if (source is null || source.ImageCount == 0) return null;
        var image = source.CreateImage(0, new CGImageOptions { ShouldCache = true });
        if (image is null) return null;
        return new NSImage(image, new CGSize(image.Width, image.Height));
    }

    /// <summary>Decodes the wallpaper file at <paramref name="path"/>, or null when it is missing or unreadable.</summary>
    public static NSImage? DecodeFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return Decode(File.ReadAllBytes(path)); }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not read the theme image {File}.", Path.GetFileName(path));
            return null;
        }
    }

    /// <summary>A JPEG thumbnail of at most <paramref name="maxPixelSize"/> pixels on its long side, or null.</summary>
    public static byte[]? CreatePreviewJpeg(byte[] data, int maxPixelSize = 350)
    {
        using var thumbnail = CreateThumbnail(data, maxPixelSize);
        if (thumbnail is null) return null;
        var output = new NSMutableData();
        using var destination = CGImageDestination.Create(output, "public.jpeg", 1);
        if (destination is null) return null;
        destination.AddImage(thumbnail, new CGImageDestinationOptions { LossyCompressionQuality = 0.85f });
        return destination.Close() ? output.ToArray() : null;
    }

    /// <summary>The average colour of the image as #RRGGBB (the Windows editor's 8×8 average), or empty.</summary>
    public static string AverageColorHex(byte[]? data)
    {
        if (data is not { Length: > 0 }) return string.Empty;
        using var thumbnail = CreateThumbnail(data, 8);
        if (thumbnail is null) return string.Empty;

        const int size = 8;
        var pixels = new byte[size * size * 4];
        using var space = CGColorSpace.CreateSrgb();
        using (var context = new CGBitmapContext(pixels, size, size, 8, size * 4, space, CGImageAlphaInfo.NoneSkipLast))
            context.DrawImage(new CGRect(0, 0, size, size), thumbnail);

        long red = 0, green = 0, blue = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            red += pixels[index];
            green += pixels[index + 1];
            blue += pixels[index + 2];
        }
        const int count = size * size;
        return $"#{red / count:X2}{green / count:X2}{blue / count:X2}";
    }

    /// <summary>
    /// Where an image of <paramref name="imageSize"/> is drawn in <paramref name="bounds"/> (a flipped or
    /// unflipped rect alike: "top" is passed in by the caller): Fill covers the bounds anchored at the
    /// alignment, Fit shows the whole image centred.
    /// </summary>
    public static CGRect Place(CGSize imageSize, CGRect bounds, ThemeWallpaperFit fit, ThemeWallpaperAlignment alignment, bool flipped)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0) return bounds;
        var scale = fit == ThemeWallpaperFit.Fit
            ? Math.Min(bounds.Width / imageSize.Width, bounds.Height / imageSize.Height)
            : Math.Max(bounds.Width / imageSize.Width, bounds.Height / imageSize.Height);
        var size = new CGSize(imageSize.Width * scale, imageSize.Height * scale);
        if (fit == ThemeWallpaperFit.Fit) alignment = ThemeWallpaperAlignment.Center;

        double horizontal = alignment switch
        {
            ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Left or ThemeWallpaperAlignment.BottomLeft => 0,
            ThemeWallpaperAlignment.TopRight or ThemeWallpaperAlignment.Right or ThemeWallpaperAlignment.BottomRight => 1,
            _ => 0.5
        };
        double vertical = alignment switch
        {
            ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Top or ThemeWallpaperAlignment.TopRight => 0,
            ThemeWallpaperAlignment.BottomLeft or ThemeWallpaperAlignment.Bottom or ThemeWallpaperAlignment.BottomRight => 1,
            _ => 0.5
        };
        // vertical: 0 = top edge, 1 = bottom edge. In an unflipped view the top is at MaxY.
        if (!flipped) vertical = 1 - vertical;
        var x = bounds.X + (bounds.Width - size.Width) * horizontal;
        var y = bounds.Y + (bounds.Height - size.Height) * vertical;
        return new CGRect(x, y, size.Width, size.Height);
    }

    private static CGImage? CreateThumbnail(byte[] data, int maxPixelSize)
    {
        using var source = CGImageSource.FromData(NSData.FromArray(data));
        if (source is null || source.ImageCount == 0) return null;
        return source.CreateThumbnail(0, new CGImageThumbnailOptions
        {
            MaxPixelSize = maxPixelSize,
            CreateThumbnailFromImageAlways = true,
            CreateThumbnailWithTransform = true
        });
    }
}
