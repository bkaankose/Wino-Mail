using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SkiaSharp;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class AccountProfilePictureFileService : IAccountProfilePictureFileService
{
    private const int ProfilePictureSize = 48;
    private const int MaximumInputBytes = 10 * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private const string ProfilePicturesSubFolder = "account-profile-pictures";

    private readonly string _profilePicturesFolder;
    private readonly ILogger _logger = Log.ForContext<AccountProfilePictureFileService>();

    public AccountProfilePictureFileService(IApplicationConfiguration applicationConfiguration)
    {
        _profilePicturesFolder = Path.Combine(applicationConfiguration.ApplicationDataFolderPath, ProfilePicturesSubFolder);
        Directory.CreateDirectory(_profilePicturesFolder);
    }

    public string GetProfilePicturePath(Guid fileId)
    {
        if (fileId == Guid.Empty)
            return null;

        var path = BuildFilePath(fileId);
        return File.Exists(path) ? path : null;
    }

    public Uri GetProfilePictureUri(Guid fileId)
        => GetProfilePicturePath(fileId) == null
            ? null
            : new Uri($"ms-appdata:///local/{ProfilePicturesSubFolder}/{fileId:N}.jpg");

    public async Task<Guid> SaveProfilePictureAsync(
        byte[] imageData,
        Guid? replacedFileId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedImage = NormalizeImage(imageData);
        var newFileId = Guid.NewGuid();
        var destinationPath = BuildFilePath(newFileId);
        var temporaryPath = destinationPath + ".tmp";

        Directory.CreateDirectory(_profilePicturesFolder);

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, normalizedImage, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath);

            if (replacedFileId is { } oldFileId && oldFileId != newFileId)
                await DeleteProfilePictureAsync(oldFileId).ConfigureAwait(false);

            return newFileId;
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(destinationPath);
            throw;
        }
    }

    public Task DeleteProfilePictureAsync(Guid fileId)
    {
        if (fileId != Guid.Empty)
        {
            TryDelete(BuildFilePath(fileId));
            foreach (var iconPath in Directory.EnumerateFiles(_profilePicturesFolder, $"{fileId:N}.icon-*.png"))
                TryDelete(iconPath);
        }

        return Task.CompletedTask;
    }

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

    private string BuildFilePath(Guid fileId) => Path.Combine(_profilePicturesFolder, $"{fileId:N}.jpg");

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete account profile picture file {Path}", path);
        }
    }
}
