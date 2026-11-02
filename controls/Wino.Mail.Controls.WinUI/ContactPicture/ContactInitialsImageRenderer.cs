using SkiaSharp;

namespace Wino.Mail.Controls.ContactPicture;

/// <summary>
/// Renders the initials avatar of <see cref="WinoContactPicture"/> as a PNG for surfaces that only take
/// image files, such as toast notifications.
/// </summary>
public static class ContactInitialsImageRenderer
{
    public static string GetCacheKey(string? name, string? address)
    {
        var initials = ContactVisual.GetInitials(name?.Trim(), address?.Trim());
        var color = ContactVisual.GetBackgroundColor(GetColorKey(name, address));

        return $"{color.R:x2}{color.G:x2}{color.B:x2}-{Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(initials))}";
    }

    public static byte[] Render(string? name, string? address, int pixelSize)
    {
        var initials = ContactVisual.GetInitials(name?.Trim(), address?.Trim());
        var color = ContactVisual.GetBackgroundColor(GetColorKey(name, address));
        var center = pixelSize / 2f;

        using var bitmap = new SKBitmap(pixelSize, pixelSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var fillPaint = new SKPaint { Color = new SKColor(color.R, color.G, color.B), IsAntialias = true };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface, pixelSize * 0.4f);

        canvas.Clear(SKColors.Transparent);
        canvas.DrawCircle(center, center, center, fillPaint);

        var metrics = font.Metrics;
        var baseline = center - ((metrics.Ascent + metrics.Descent) / 2f);
        canvas.DrawText(initials, center, baseline, SKTextAlign.Center, font, textPaint);

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Initials avatar rendering failed.");
        return encoded.ToArray();
    }

    // Matches the normalization and background key of WinoContactPicture, so a sender keeps the colour they have in the mail list.
    private static string GetColorKey(string? name, string? address)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        return trimmedName.Length > 0 ? trimmedName : address?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
