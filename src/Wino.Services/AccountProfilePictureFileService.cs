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

    public Uri GetProfilePictureIconUri(Guid fileId, string accountColorHex)
    {
        var sourcePath = GetProfilePicturePath(fileId);
        if (sourcePath == null)
            return null;

        var hasBorderColor = SKColor.TryParse(accountColorHex, out var borderColor);
        var colorKey = hasBorderColor ? borderColor.ToString()[1..] : "none";
        var iconFileName = $"{fileId:N}.icon-{colorKey}.png";
        var iconPath = Path.Combine(_profilePicturesFolder, iconFileName);

        if (!File.Exists(iconPath))
            CreateProfilePictureIcon(sourcePath, iconPath, hasBorderColor ? borderColor : null);

        return new Uri($"ms-appdata:///local/{ProfilePicturesSubFolder}/{iconFileName}");
    }

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

    private static void CreateProfilePictureIcon(string sourcePath, string destinationPath, SKColor? borderColor)
    {
        using var source = SKBitmap.Decode(sourcePath)
            ?? throw new InvalidOperationException("Profile picture icon source could not be decoded.");
        using var icon = new SKBitmap(ProfilePictureSize, ProfilePictureSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(icon);
        using var circlePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var imagePaint = new SKPaint { BlendMode = SKBlendMode.SrcIn, IsAntialias = true };

        var bounds = new SKRect(0, 0, ProfilePictureSize, ProfilePictureSize);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawOval(bounds, circlePaint);
        canvas.DrawBitmap(source, bounds, bounds, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), imagePaint);

        if (borderColor is { } color)
        {
            using var borderPaint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawOval(new SKRect(1, 1, ProfilePictureSize - 1, ProfilePictureSize - 1), borderPaint);
        }

        using var encoded = icon.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Profile picture icon rendering failed.");
        using var output = File.Create(destinationPath);
        encoded.SaveTo(output);
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
