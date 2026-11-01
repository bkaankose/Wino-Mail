using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services.WidgetsAndTiles;

/// <summary>
/// Turns stored contact pictures into small square JPEG data URIs for the People widget. The Board renders only
/// images it is handed inline, and full-size contact photos would bloat the snapshot.
/// </summary>
public sealed class ContactPictureThumbnailer(IPictureStorageService pictureStorageService)
{
    // The Board draws avatars at 40 px; every widget update carries these bytes inline, so they are kept small.
    private const uint Size = 64;
    private const float JpegQuality = 0.8f;
    private const long MaximumSourceBytes = 8 * 1024 * 1024;

    private readonly ConcurrentDictionary<Guid, string> _cache = new();

    /// <summary>Returns a data URI, or an empty string when the contact has no usable picture.</summary>
    public async Task<string> GetAsync(Guid? pictureFileId)
    {
        if (pictureFileId is not { } fileId)
            return string.Empty;

        if (_cache.TryGetValue(fileId, out var cached))
            return cached;

        // Failures are not cached: a picture that is still being written during contact sync is read on the next publish.
        var dataUri = await CreateAsync(fileId);
        if (dataUri.Length > 0)
            _cache[fileId] = dataUri;

        return dataUri;
    }

    private async Task<string> CreateAsync(Guid fileId)
    {
        try
        {
            var path = pictureStorageService.GetPicturePath(PictureKind.Contact, fileId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length > MaximumSourceBytes)
                return string.Empty;

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var source = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);

            var scale = (double)Size / Math.Min(decoder.PixelWidth, decoder.PixelHeight);
            var scaledWidth = (uint)Math.Max(Size, Math.Round(decoder.PixelWidth * scale));
            var scaledHeight = (uint)Math.Max(Size, Math.Round(decoder.PixelHeight * scale));

            using var output = new InMemoryRandomAccessStream();
            var encoderOptions = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(JpegQuality, Windows.Foundation.PropertyType.Single)
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, encoderOptions);
            var pixels = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform
                {
                    ScaledWidth = scaledWidth,
                    ScaledHeight = scaledHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Bounds = new BitmapBounds
                    {
                        X = (scaledWidth - Size) / 2,
                        Y = (scaledHeight - Size) / 2,
                        Width = Size,
                        Height = Size
                    }
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            encoder.SetSoftwareBitmap(pixels);
            await encoder.FlushAsync();

            var bytes = new byte[output.Size];
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);

            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
