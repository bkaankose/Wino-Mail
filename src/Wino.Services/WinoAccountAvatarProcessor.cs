using SkiaSharp;
using Wino.Mail.Api.Contracts.Common;
using Wino.Core.Domain.Exceptions;
using System;

namespace Wino.Services;

internal static class WinoAccountAvatarProcessor
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPixels = 25_000_000;

    public static byte[] Normalize(byte[] input)
    {
        if (input.Length > MaxBytes) throw new WinoAccountApiException(ApiErrorCodes.AvatarTooLarge);
        using var stream = new SKMemoryStream(input);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png)
            || codec.Info.Width <= 0 || codec.Info.Height <= 0 || codec.FrameCount > 1)
            throw new WinoAccountApiException(ApiErrorCodes.AvatarInvalid);
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
            throw new WinoAccountApiException(ApiErrorCodes.AvatarTooLarge);

        using var bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new WinoAccountApiException(ApiErrorCodes.AvatarInvalid);

        using var output = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(128, 128);
        // Apply EXIF orientation around the center of the square crop.
        switch (codec.EncodedOrigin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.RotateDegrees(270); break;
        }
        var side = Math.Min(bitmap.Width, bitmap.Height);
        var left = (bitmap.Width - side) / 2f;
        var top = (bitmap.Height - side) / 2f;
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(left, top, left + side, top + side),
            new SKRect(-128, -128, 128, 128), new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        using var normalized = SKImage.FromBitmap(output);
        using var encoded = normalized.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
