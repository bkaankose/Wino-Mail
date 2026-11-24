using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Wino.Editor;
using Wino.Renderer.Editing;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Prepares images for the composer. PNG, JPEG and GIF bytes are inserted as they are; any other format Windows can
/// decode is converted to PNG, which every mail client shows.
/// </summary>
internal static class EngineEditorImages
{

    /// <summary>
    /// <see cref="EditorImageInfo.Data"/> is a <c>data:</c> URI; bare base64 is accepted too. Null when the image
    /// cannot be read.
    /// </summary>
    public static Task<ImageArgs?> CreateAsync(EditorImageInfo image)
    {
        if (EngineEditorHtml.TryReadDataImage(image.Data, out _, out var bytes))
            return CreateAsync(bytes, image.Name);

        try
        {
            return CreateAsync(Convert.FromBase64String(image.Data.Trim()), image.Name);
        }
        catch (FormatException)
        {
            return Task.FromResult<ImageArgs?>(null);
        }
    }

    public static async Task<ImageArgs?> CreateAsync(byte[] bytes, string? fileName)
    {
        if (bytes.Length == 0) return null;

        if (DetectMediaType(bytes) is { } mediaType)
            return new ImageArgs(bytes, mediaType, fileName, null);

        var png = await ConvertToPngAsync(bytes);
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

    private static async Task<byte[]?> ConvertToPngAsync(byte[] encoded)
    {
        try
        {
            using var source = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(encoded);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            source.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(source);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            if (output.Size == 0) return null;

            using var input = output.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            var loaded = await reader.LoadAsync((uint)output.Size);
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException)
        {
            // Not an image Windows can decode.
            return null;
        }
    }
}
