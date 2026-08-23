using FluentAssertions;
using Moq;
using SkiaSharp;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountProfilePictureFileServiceTests : IDisposable
{
    private readonly string _tempFolder = Path.Combine(Path.GetTempPath(), $"WinoAccountPictures_{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveProfilePictureAsync_NormalizesTo48Pixels_AndReturnsLocalUri()
    {
        var service = CreateService();

        var fileId = await service.SaveProfilePictureAsync(CreateImage(120, 80));
        var path = service.GetProfilePicturePath(fileId);

        path.Should().NotBeNull();
        using var bitmap = SKBitmap.Decode(path);
        bitmap.Width.Should().Be(48);
        bitmap.Height.Should().Be(48);
        service.GetProfilePictureUri(fileId).ToString()
            .Should().Be($"ms-appdata:///local/account-profile-pictures/{fileId:N}.jpg");
    }

    [Fact]
    public async Task SaveProfilePictureAsync_ReplacesOldFileOnlyAfterNewFileIsValid()
    {
        var service = CreateService();
        var oldFileId = await service.SaveProfilePictureAsync(CreateImage(48, 48));

        var act = () => service.SaveProfilePictureAsync([1, 2, 3], oldFileId);

        await act.Should().ThrowAsync<ArgumentException>();
        service.GetProfilePicturePath(oldFileId).Should().NotBeNull();

        var newFileId = await service.SaveProfilePictureAsync(CreateImage(96, 96), oldFileId);
        service.GetProfilePicturePath(oldFileId).Should().BeNull();
        service.GetProfilePicturePath(newFileId).Should().NotBeNull();
    }

    [Fact]
    public async Task GetProfilePictureIconUri_RendersCircularPng_WithOptionalAccountColorBorder()
    {
        var service = CreateService();
        var fileId = await service.SaveProfilePictureAsync(CreateImage(96, 96));

        var plainUri = service.GetProfilePictureIconUri(fileId, null);
        var borderedUri = service.GetProfilePictureIconUri(fileId, "#FF0000");
        var plainPath = Path.Combine(_tempFolder, "account-profile-pictures", Path.GetFileName(plainUri.LocalPath));
        var borderedPath = Path.Combine(_tempFolder, "account-profile-pictures", Path.GetFileName(borderedUri.LocalPath));

        using var plainIcon = SKBitmap.Decode(plainPath);
        using var borderedIcon = SKBitmap.Decode(borderedPath);
        plainIcon.GetPixel(0, 0).Alpha.Should().Be(0);
        borderedIcon.GetPixel(0, 0).Alpha.Should().Be(0);
        borderedIcon.GetPixel(24, 1).Red.Should().BeGreaterThan(200);
        borderedIcon.GetPixel(24, 1).Green.Should().BeLessThan(50);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
            Directory.Delete(_tempFolder, recursive: true);
    }

    private AccountProfilePictureFileService CreateService()
    {
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(item => item.ApplicationDataFolderPath).Returns(_tempFolder);
        return new AccountProfilePictureFileService(configuration.Object);
    }

    private static byte[] CreateImage(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
