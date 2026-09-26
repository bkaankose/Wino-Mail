using Moq;
using Wino.Core.Domain.Enums;
using FluentAssertions;
using System.Threading;
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
        var notificationBuilder = new Mock<INotificationBuilder>();
        var viewModel = CreateViewModel(
            dialogService.Object,
            accountService.Object,
            synchronizationManager.Object,
            notificationBuilder.Object);
        viewModel.Account = account;

        await viewModel.DeleteAccountCommand.ExecuteAsync(null);

        synchronizationManager.Verify(service => service.DestroySynchronizerAsync(account.Id), Times.Once);
        accountService.Verify(service => service.DeleteAccountAsync(account), Times.Once);
        notificationBuilder.Verify(service => service.UpdateTaskbarIconBadgeAsync(), Times.Once);
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

    [Fact]
    public async Task ApplyCapabilities_WhenCalendarIsTurnedOff_AsksBeforeRemovingCalendarData()
    {
        var account = CreateOutlookAccount();
        var dialogService = new Mock<IMailDialogService>();
        dialogService
            .Setup(service => service.ShowConfirmationDialogAsync(
                Translator.AccountDetailsPage_DisableCalendarConfirmation,
                Translator.AccountDetailsPage_CalendarTransitionTitle,
                Translator.Buttons_Apply))
            .ReturnsAsync(true);
        var capabilityService = new Mock<IAccountCapabilityService>();
        capabilityService
            .Setup(service => service.ApplyAsync(account, true, false, true, true, default))
            .ReturnsAsync(account);
        var viewModel = CreateViewModel(
            dialogService.Object,
            Mock.Of<IAccountService>(),
            Mock.Of<ISynchronizationManager>(),
            capabilityService: capabilityService.Object);
        viewModel.Account = account;

        viewModel.IsCalendarCapabilitySelected = false;
        await viewModel.ApplyCapabilitiesCommand.ExecuteAsync(null);

        capabilityService.Verify(service => service.ApplyAsync(account, true, false, true, true, default), Times.Once);
    }

    [Fact]
    public async Task ApplyCapabilities_WhenTheUserDeclines_RestoresEverySelectionAndChangesNothing()
    {
        var account = CreateOutlookAccount();
        var dialogService = new Mock<IMailDialogService>();
        dialogService
            .Setup(service => service.ShowConfirmationDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var capabilityService = new Mock<IAccountCapabilityService>();
        var viewModel = CreateViewModel(
            dialogService.Object,
            Mock.Of<IAccountService>(),
            Mock.Of<ISynchronizationManager>(),
            capabilityService: capabilityService.Object);
        viewModel.Account = account;

        viewModel.IsMailCapabilitySelected = false;
        viewModel.IsCalendarCapabilitySelected = false;
        await viewModel.ApplyCapabilitiesCommand.ExecuteAsync(null);

        // Two transitions share one dialog under the generic title.
        dialogService.Verify(service => service.ShowConfirmationDialogAsync(
            It.Is<string>(question => question.Contains(Translator.AccountDetailsPage_DisableMailConfirmation) && question.Contains(Translator.AccountDetailsPage_DisableCalendarConfirmation)),
            Translator.AccountDetailsPage_CapabilityTransitionTitle,
            Translator.Buttons_Apply), Times.Once);
        capabilityService.Verify(service => service.ApplyAsync(
            It.IsAny<MailAccount>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        viewModel.IsMailCapabilitySelected.Should().BeTrue();
        viewModel.IsCalendarCapabilitySelected.Should().BeTrue();
        viewModel.IsCapabilitySelectionChanged.Should().BeFalse();
    }

    private static MailAccount CreateOutlookAccount() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Work",
        ProviderType = MailProviderType.Outlook,
        IsMailAccessGranted = true,
        IsCalendarAccessGranted = true,
        IsContactAccessGranted = true,
        IsTaskAccessGranted = true
    };

    private static AccountDetailsPageViewModel CreateViewModel(
        IMailDialogService dialogService,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager,
        INotificationBuilder? notificationBuilder = null,
        IAccountCapabilityService? capabilityService = null)
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
            Mock.Of<IMailServerTestService>(),
            notificationBuilder ?? Mock.Of<INotificationBuilder>(),
            Mock.Of<IApplicationConfiguration>(),
            Mock.Of<IFileService>(),
            Mock.Of<IPictureStorageService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            capabilityService ?? Mock.Of<IAccountCapabilityService>(),
            synchronizationManager);
    }
}
