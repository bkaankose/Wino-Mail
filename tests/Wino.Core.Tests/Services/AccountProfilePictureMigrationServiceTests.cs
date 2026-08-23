using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using SkiaSharp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountProfilePictureMigrationServiceTests : IAsyncLifetime
{
    private readonly string _tempFolder = Path.Combine(Path.GetTempPath(), $"WinoProfileMigration_{Guid.NewGuid():N}");
    private InMemoryDatabaseService _databaseService = null!;

    [Fact]
    public async Task RunAsync_MigratesLegacyBase64WithoutChangingContacts_AndIsIdempotent()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "owner@test.local",
            Base64ProfilePictureData = Convert.ToBase64String(CreateImage())
        };
        await _databaseService.Connection.InsertAsync(account);
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(item => item.ApplicationDataFolderPath).Returns(_tempFolder);
        var fileService = new AccountProfilePictureFileService(configuration.Object);
        var migration = new AccountProfilePictureMigrationService(
            _databaseService,
            fileService,
            Mock.Of<IMessenger>());

        await migration.RunAsync();
        await migration.RunAsync();

        var migrated = await _databaseService.Connection.FindAsync<MailAccount>(account.Id);
        migrated.Base64ProfilePictureData.Should().BeEmpty();
        migrated.ProfilePictureFileId.Should().NotBeNull();
        migrated.IsProfilePictureBackfillComplete.Should().BeTrue();
        fileService.GetProfilePicturePath(migrated.ProfilePictureFileId!.Value).Should().NotBeNull();
        Directory.GetFiles(Path.Combine(_tempFolder, "account-profile-pictures")).Should().ContainSingle();
        (await _databaseService.Connection.Table<AccountContact>().CountAsync()).Should().Be(0);
    }

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
        if (Directory.Exists(_tempFolder))
            Directory.Delete(_tempFolder, recursive: true);
    }

    private static byte[] CreateImage()
    {
        using var bitmap = new SKBitmap(72, 48);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.MediumPurple);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
