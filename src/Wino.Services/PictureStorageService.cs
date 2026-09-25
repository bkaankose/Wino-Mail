using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SkiaSharp;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Stores pictures as JPEG files under the application data folder.
/// Contact pictures: contacts/{guid}.jpg, raw bytes.
/// Account profile pictures: account-profile-pictures/{guid:N}.jpg, normalized to 48x48.
/// The two file name formats are kept as-is so existing files still resolve.
/// </summary>
public sealed class PictureStorageService : IPictureStorageService
{
    private const int ProfilePictureSize = 48;
    private const int MaximumInputBytes = 10 * 1024 * 1024;
    private const int MaximumDimension = 8192;

    private readonly string _dataFolder;
    private readonly ILogger _logger = Log.ForContext<PictureStorageService>();

    public PictureStorageService(IApplicationConfiguration applicationConfiguration)
    {
        _dataFolder = applicationConfiguration.ApplicationDataFolderPath;

        foreach (var kind in new[] { PictureKind.Contact, PictureKind.AccountProfile })
            Directory.CreateDirectory(GetFolder(kind));
    }

    public string GetPicturePath(PictureKind kind, Guid fileId)
    {
        if (fileId == Guid.Empty)
            return null;

        var path = BuildFilePath(kind, fileId);
        return File.Exists(path) ? path : null;
    }

    public Uri GetPictureUri(PictureKind kind, Guid fileId)
        => GetPicturePath(kind, fileId) == null
            ? null
            : new Uri($"ms-appdata:///local/{GetSubFolder(kind)}/{GetFileName(kind, fileId)}");

    public async Task<Guid> SavePictureAsync(
        PictureKind kind,
        byte[] imageData,
        Guid? replacedFileId = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = kind == PictureKind.AccountProfile ? NormalizeImage(imageData) : imageData;
        var newFileId = Guid.NewGuid();
        var destinationPath = BuildFilePath(kind, newFileId);
        var temporaryPath = destinationPath + ".tmp";

        // The folder may have been removed while the app runs; recreate it before writing.
        Directory.CreateDirectory(GetFolder(kind));

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath);

            if (replacedFileId is { } oldFileId && oldFileId != newFileId)
                await DeletePictureAsync(kind, oldFileId).ConfigureAwait(false);

            return newFileId;
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(destinationPath);
            throw;
        }
    }

    public Task DeletePictureAsync(PictureKind kind, Guid fileId)
    {
        if (fileId != Guid.Empty)
        {
            TryDelete(BuildFilePath(kind, fileId));

            if (kind == PictureKind.AccountProfile && Directory.Exists(GetFolder(kind)))
            {
                foreach (var iconPath in Directory.EnumerateFiles(GetFolder(kind), $"{fileId:N}.icon-*.png"))
                    TryDelete(iconPath);
            }
        }

        return Task.CompletedTask;
    }

    private static string GetSubFolder(PictureKind kind) => kind switch
    {
        PictureKind.Contact => "contacts",
        PictureKind.AccountProfile => "account-profile-pictures",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string GetFileName(PictureKind kind, Guid fileId)
        => kind == PictureKind.AccountProfile ? $"{fileId:N}.jpg" : $"{fileId:D}.jpg";

    private string GetFolder(PictureKind kind) => Path.Combine(_dataFolder, GetSubFolder(kind));

    private string BuildFilePath(PictureKind kind, Guid fileId) => Path.Combine(GetFolder(kind), GetFileName(kind, fileId));

    private static byte[] NormalizeImage(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0 || imageData.Length > MaximumInputBytes)
            throw new ArgumentException("Profile picture data is empty or exceeds the supported size.", nameof(imageData));

        using var inputStream = new SKMemoryStream(imageData);
        using var codec = SKCodec.Create(inputStream)
            ?? throw new ArgumentException("Profile picture data is not a supported image.", nameof(imageData));

        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            codec.Info.Width > MaximumDimension || codec.Info.Height > MaximumDimension)
            throw new ArgumentException("Profile picture dimensions are not supported.", nameof(imageData));

        using var source = SKBitmap.Decode(codec)
            ?? throw new ArgumentException("Profile picture data could not be decoded.", nameof(imageData));

        var cropSize = Math.Min(source.Width, source.Height);
        var sourceRect = new SKRectI(
            (source.Width - cropSize) / 2,
            (source.Height - cropSize) / 2,
            (source.Width + cropSize) / 2,
            (source.Height + cropSize) / 2);

        using var normalized = new SKBitmap(ProfilePictureSize, ProfilePictureSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(
                source,
                sourceRect,
                new SKRect(0, 0, ProfilePictureSize, ProfilePictureSize),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        using var encoded = normalized.Encode(SKEncodedImageFormat.Jpeg, 90)
            ?? throw new InvalidOperationException("Profile picture normalization failed.");

        return encoded.ToArray();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete picture file {Path}", path);
        }
    }
}
