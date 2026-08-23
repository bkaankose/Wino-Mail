using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountProfilePictureBackfillServiceTests
{
    [Fact]
    public async Task RunAsync_ProcessesOnlyUnresolvedOAuthAccounts_OneAtATime()
    {
        var gmail = CreateAccount(MailProviderType.Gmail, isComplete: false);
        var resolvedOutlook = CreateAccount(MailProviderType.Outlook, isComplete: true);
        var imap = CreateAccount(MailProviderType.IMAP4, isComplete: false);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([gmail, resolvedOutlook, imap]);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.SynchronizeProfileAsync(gmail.Id, default))
            .ReturnsAsync(MailSynchronizationResult.Completed(
                new ProfileInformation("Gmail", ProfilePictureFetchResult.ConfirmedAbsent, gmail.Address)));
        var fileService = new Mock<IAccountProfilePictureFileService>();
        var service = new AccountProfilePictureBackfillService(
            accountService.Object,
            fileService.Object,
            synchronizationManager.Object);

        await service.RunAsync();

        synchronizationManager.Verify(manager => manager.SynchronizeProfileAsync(gmail.Id, default), Times.Once);
        synchronizationManager.Verify(manager => manager.SynchronizeProfileAsync(resolvedOutlook.Id, default), Times.Never);
        synchronizationManager.Verify(manager => manager.SynchronizeProfileAsync(imap.Id, default), Times.Never);
        accountService.Verify(service => service.UpdateProfileInformationAsync(
            gmail.Id,
            It.Is<ProfileInformation>(profile => profile.ProfilePicture.Status == ProfilePictureFetchStatus.ConfirmedAbsent)), Times.Once);
    }

    private static MailAccount CreateAccount(MailProviderType providerType, bool isComplete)
        => new()
        {
            Id = Guid.NewGuid(),
            Address = $"{providerType}@test.local",
            ProviderType = providerType,
            IsProfilePictureBackfillComplete = isComplete
        };
}
