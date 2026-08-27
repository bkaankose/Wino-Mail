using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ProviderSelectionPageViewModelTests
{
    [Fact]
    public void ICloudContactSources_IncludeCardDavAndLocal()
    {
        var viewModel = new ProviderSelectionPageViewModel(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IProviderService>(),
            Mock.Of<INewThemeService>(),
            new WelcomeWizardContext())
        {
            SelectedProvider = new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.iCloud)
        };

        viewModel.IsDavContactChoiceAvailable.Should().BeTrue();
        viewModel.ContactSourceOptions.Should().Equal(
            Translator.ProviderSelection_SourceCardDav,
            Translator.ProviderSelection_SourceLocalContacts);
    }

    private static ProviderSelectionPageViewModel CreateViewModel(MailProviderType type, SpecialImapProvider specialImapProvider)
        => new(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IProviderService>(),
            Mock.Of<INewThemeService>(),
            new WelcomeWizardContext())
        {
            SelectedProvider = new ProviderDetail(type, specialImapProvider)
        };

    [Fact]
    public void OAuthProvider_DefaultsToMailAndContactsSyncedWithProvider()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.MailMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Off);
        viewModel.TaskMode.Should().Be(AccountCapabilityMode.Off);
        viewModel.IsCapabilitySelectionMissing.Should().BeFalse();
    }

    [Fact]
    public void ImapProvider_DoesNotOfferProviderTasks()
    {
        var viewModel = CreateViewModel(MailProviderType.IMAP4, SpecialImapProvider.iCloud);

        viewModel.IsTaskProviderModeAvailable.Should().BeFalse();
        viewModel.IsTaskProviderUnavailableHintVisible.Should().BeTrue();
        viewModel.TaskMode.Should().NotBe(AccountCapabilityMode.Provider);
    }

    [Fact]
    public void CapabilityModes_DriveAccessFlagsAndPermissionSummary()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.MailMode = AccountCapabilityMode.Off;
        viewModel.ContactMode = AccountCapabilityMode.Local;

        viewModel.IsMailAccessEnabled.Should().BeFalse();
        viewModel.IsContactAccessEnabled.Should().BeTrue();
        viewModel.ProviderPermissionScopes.Should().BeEmpty();
        viewModel.LocalOnlyCapabilities.Should().Equal(Translator.ProviderSelection_UseForContacts);
    }

    [Fact]
    public void AllCapabilitiesOff_BlocksContinue()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.MailMode = AccountCapabilityMode.Off;
        viewModel.CalendarMode = AccountCapabilityMode.Off;
        viewModel.ContactMode = AccountCapabilityMode.Off;
        viewModel.TaskMode = AccountCapabilityMode.Off;

        viewModel.IsCapabilitySelectionMissing.Should().BeTrue();
    }

    [Fact]
    public void ModeIndex_IgnoresTransientNegativeSelection()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.CalendarModeIndex = 2;
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);

        viewModel.CalendarModeIndex = -1;
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);
    }
}
