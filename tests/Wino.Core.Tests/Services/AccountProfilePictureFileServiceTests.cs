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
