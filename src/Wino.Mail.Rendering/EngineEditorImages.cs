using System;
using System.IO;
using Wino.Editor;
using Wino.Renderer.Editing;
using Wino.Renderer.Platform;

namespace Wino.Mail.Rendering;

/// <summary>
/// Prepares images for the composer. PNG, JPEG and GIF bytes are inserted as they are; any other format the platform
/// decodes (HEIC, TIFF, BMP, WebP) is converted to PNG through the engine's codec, which every mail client shows.
/// </summary>
internal static class EngineEditorImages
{
    /// <summary>
    /// <see cref="EditorImageInfo.Data"/> is a <c>data:</c> URI; bare base64 is accepted too. Null when the image
    /// cannot be read.
    /// </summary>
    public static ImageArgs? Create(EditorImageInfo image, IImageCodec codec)
    {
        if (EngineEditorHtml.TryReadDataImage(image.Data, out _, out var bytes))
            return Create(bytes, image.Name, codec);

        try
        {
            return Create(Convert.FromBase64String(image.Data.Trim()), image.Name, codec);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static ImageArgs? Create(byte[] bytes, string? fileName, IImageCodec codec)
    {
        if (bytes.Length == 0) return null;

        if (DetectMediaType(bytes) is { } mediaType)
            return new ImageArgs(bytes, mediaType, fileName, null);

        var png = codec.TranscodeToPng(bytes);
        if (png is null) return null;

        var pngName = string.IsNullOrWhiteSpace(fileName) ? null : Path.ChangeExtension(fileName, ".png");
        return new ImageArgs(png, "image/png", pngName, null);
    }

    private static string? DetectMediaType(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return "image/png";

        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length > 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
            return "image/gif";

        return null;
    }
}
