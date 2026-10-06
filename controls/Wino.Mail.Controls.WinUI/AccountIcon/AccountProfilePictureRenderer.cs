using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Wino.Mail.Controls.AccountIcon;

internal static class AccountProfilePictureRenderer
{
    public const int IconPixelSize = 48;
    public const int MaximumInputBytes = 10 * 1024 * 1024;
    public const int MaximumDimension = 8192;

    public static byte[] Render(byte[] imageData, string? accountColorHex)
    {
        if (imageData is not { Length: > 0 } || imageData.Length > MaximumInputBytes)
        {
            throw new ArgumentException("Profile picture data is empty or exceeds the supported size.", nameof(imageData));
        }

        using var inputStream = new SKMemoryStream(imageData);
        using var codec = SKCodec.Create(inputStream)
            ?? throw new ArgumentException("Profile picture data is not a supported image.", nameof(imageData));

        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            codec.Info.Width > MaximumDimension || codec.Info.Height > MaximumDimension)
        {
            throw new ArgumentException("Profile picture dimensions are not supported.", nameof(imageData));
        }

        using var source = SKBitmap.Decode(codec)
            ?? throw new ArgumentException("Profile picture data could not be decoded.", nameof(imageData));
        using var icon = new SKBitmap(IconPixelSize, IconPixelSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(icon);
        using var imagePaint = new SKPaint { IsAntialias = true };
        using var clipPathBuilder = new SKPathBuilder();

        var cropSize = Math.Min(source.Width, source.Height);
        var sourceRect = new SKRectI(
            (source.Width - cropSize) / 2,
            (source.Height - cropSize) / 2,
            (source.Width + cropSize) / 2,
            (source.Height + cropSize) / 2);
        var destinationRect = new SKRect(0, 0, IconPixelSize, IconPixelSize);

        canvas.Clear(SKColors.Transparent);
        clipPathBuilder.AddCircle(IconPixelSize / 2f, IconPixelSize / 2f, IconPixelSize / 2f);
        using var clipPath = clipPathBuilder.Detach();
        canvas.Save();
        canvas.ClipPath(clipPath, antialias: true);
        canvas.DrawBitmap(
            source,
            sourceRect,
            destinationRect,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            imagePaint);
        canvas.Restore();

        if (TryParseColor(accountColorHex, out var borderColor))
        {
            using var borderPaint = new SKPaint
            {
                Color = borderColor,
                IsAntialias = true,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
            };

            canvas.DrawCircle(IconPixelSize / 2f, IconPixelSize / 2f, (IconPixelSize / 2f) - 1, borderPaint);
        }

        using var encoded = icon.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Profile picture icon rendering failed.");
        return encoded.ToArray();
    }

    public static string GetCacheKey(byte[] imageData, string? accountColorHex)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(imageData);
        hash.AppendData(Encoding.UTF8.GetBytes(NormalizeColor(accountColorHex) ?? "none"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool TryParseColor(string? accountColorHex, out SKColor color)
    {
        var normalizedColor = NormalizeColor(accountColorHex);
        return SKColor.TryParse(normalizedColor, out color);
    }

    private static string? NormalizeColor(string? accountColorHex)
    {
        if (string.IsNullOrWhiteSpace(accountColorHex))
        {
            return null;
        }

        var value = accountColorHex.Trim();
        if (value.Length is not (7 or 9) || value[0] != '#')
        {
            return null;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return null;
            }
        }

        return value.ToUpperInvariant();
    }
}
