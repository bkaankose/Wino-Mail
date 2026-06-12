using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class FolderServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private FolderService _folderService = null!;
    private MailAccount _account = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Outlook Test",
            Address = "me@outlook.test",
            SenderName = "Test User",
            ProviderType = MailProviderType.Outlook
        };

        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));

        var accountService = CreateAccountService(_databaseService);
        _folderService = new FolderService(_databaseService, accountService, new MailCategoryService(_databaseService));
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task InsertFolderAsync_ForExistingFolder_PreservesSynchronizationState()
    {
        var folderId = Guid.NewGuid();
        var lastSynchronizedDate = DateTime.UtcNow.AddMinutes(-5);

        await _databaseService.Connection.InsertAsync(new MailItemFolder
        {
            Id = folderId,
            MailAccountId = _account.Id,
            FolderName = "Inbox",
            RemoteFolderId = "remote-inbox",
            ParentRemoteFolderId = "old-parent",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSynchronizationEnabled = true,
            DeltaToken = "https://graph.microsoft.com/v1.0/me/mailFolders/remote-inbox/messages/delta?$deltatoken=state",
            LastSynchronizedDate = lastSynchronizedDate,
            UidValidity = 42,
            HighestModeSeq = 123,
            HighestKnownUid = 456,
            LastUidReconcileUtc = DateTime.UtcNow.AddHours(-1)
        }, typeof(MailItemFolder));

        await _folderService.InsertFolderAsync(new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "Inbox Renamed By Server",
            RemoteFolderId = "remote-inbox",
            ParentRemoteFolderId = "new-parent",
            IsSynchronizationEnabled = true
        });

        var updatedFolder = await _databaseService.Connection.Table<MailItemFolder>()
            .FirstAsync(a => a.Id == folderId);

        updatedFolder.FolderName.Should().Be("Inbox Renamed By Server");
        updatedFolder.ParentRemoteFolderId.Should().Be("new-parent");
        updatedFolder.DeltaToken.Should().Be("https://graph.microsoft.com/v1.0/me/mailFolders/remote-inbox/messages/delta?$deltatoken=state");
        updatedFolder.LastSynchronizedDate.Should().Be(lastSynchronizedDate);
        updatedFolder.UidValidity.Should().Be(42);
        updatedFolder.HighestModeSeq.Should().Be(123);
        updatedFolder.HighestKnownUid.Should().Be(456);
        updatedFolder.LastUidReconcileUtc.Should().NotBeNull();
    }

    private static AccountService CreateAccountService(InMemoryDatabaseService databaseService)
    {
        var signatureService = new Mock<ISignatureService>();
        signatureService
            .Setup(a => a.CreateDefaultSignatureAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid accountId) => new AccountSignature
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                Name = "Default",
                HtmlBody = string.Empty
            });

        return new AccountService(
            databaseService,
            signatureService.Object,
            Mock.Of<IAuthenticationProvider>(),
            Mock.Of<IMimeFileService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IContactPictureFileService>());
    }
}
