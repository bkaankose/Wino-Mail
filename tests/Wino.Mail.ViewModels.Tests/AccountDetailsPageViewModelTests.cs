using Moq;
using Wino.Core.Diagnostics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class AccountDetailsPageViewModelTests
{
    [Fact]
    public async Task DeleteAccount_AfterConfirmation_StopsSynchronizationAndDeletesAccount()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Personal"
        };
        var dialogService = new Mock<IMailDialogService>();
        dialogService
            .Setup(service => service.ShowConfirmationDialogAsync(
                string.Format(Translator.DialogMessage_DeleteAccountConfirmationMessage, account.Name),
                Translator.DialogMessage_DeleteAccountConfirmationTitle,
                Translator.Buttons_Delete))
            .ReturnsAsync(true);
        var accountService = new Mock<IAccountService>();
        var synchronizationManager = new Mock<ISynchronizationManager>();
        var viewModel = CreateViewModel(dialogService.Object, accountService.Object, synchronizationManager.Object);
        viewModel.Account = account;

        await viewModel.DeleteAccountCommand.ExecuteAsync(null);

        synchronizationManager.Verify(service => service.DestroySynchronizerAsync(account.Id), Times.Once);
        accountService.Verify(service => service.DeleteAccountAsync(account), Times.Once);
    }

    [Fact]
    public async Task DeleteAccount_WhenConfirmationIsCancelled_DoesNotDeleteAccount()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Personal"
        };
        var dialogService = new Mock<IMailDialogService>();
        dialogService
            .Setup(service => service.ShowConfirmationDialogAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(false);
        var accountService = new Mock<IAccountService>();
        var synchronizationManager = new Mock<ISynchronizationManager>();
        var viewModel = CreateViewModel(dialogService.Object, accountService.Object, synchronizationManager.Object);
        viewModel.Account = account;

        await viewModel.DeleteAccountCommand.ExecuteAsync(null);

        synchronizationManager.Verify(service => service.DestroySynchronizerAsync(It.IsAny<Guid>()), Times.Never);
        accountService.Verify(service => service.DeleteAccountAsync(It.IsAny<MailAccount>()), Times.Never);
    }

    private static AccountDetailsPageViewModel CreateViewModel(
        IMailDialogService dialogService,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager)
    {
        var themeService = new Mock<INewThemeService>();
        themeService.Setup(service => service.GetAvailableAccountColors()).Returns([]);

        return new AccountDetailsPageViewModel(
            dialogService,
            accountService,
            Mock.Of<IFolderService>(),
            Mock.Of<ICalendarService>(),
            Mock.Of<IStatePersistanceService>(),
            themeService.Object,
            Mock.Of<IImapTestService>(),
            Mock.Of<INotificationBuilder>(),
            Mock.Of<IApplicationConfiguration>(),
            Mock.Of<IFileService>(),
            Mock.Of<IAccountProfilePictureFileService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IAccountCapabilityService>(),
            synchronizationManager);
    }
}
