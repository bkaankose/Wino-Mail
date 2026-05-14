using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class ContactPictureFileServiceTests : IDisposable
{
    private readonly string _tempFolder = Path.Combine(Path.GetTempPath(), $"WinoContactPictures_{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
            Directory.Delete(_tempFolder, true);
    }

    [Fact]
    public async Task SaveContactPictureAsync_RecreatesContactsFolder_WhenFolderWasDeleted()
    {
        var service = CreateService();
        var contactsFolder = Path.Combine(_tempFolder, "contacts");

        Directory.Delete(contactsFolder, true);

        var imageData = new byte[] { 1, 2, 3 };

        var fileId = await service.SaveContactPictureAsync(imageData);

        var savedPath = Path.Combine(contactsFolder, $"{fileId}.jpg");
        File.Exists(savedPath).Should().BeTrue();
        var savedData = await File.ReadAllBytesAsync(savedPath);
        savedData.Should().Equal(imageData);
    }

    private ContactPictureFileService CreateService()
    {
        var applicationConfiguration = new Mock<IApplicationConfiguration>();
        applicationConfiguration.SetupGet(a => a.ApplicationDataFolderPath).Returns(_tempFolder);

        return new ContactPictureFileService(
            Mock.Of<IDatabaseService>(),
            applicationConfiguration.Object);
    }
}
