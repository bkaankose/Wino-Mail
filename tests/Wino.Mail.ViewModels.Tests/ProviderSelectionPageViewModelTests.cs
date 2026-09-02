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
    public void OAuthProvider_DefaultsToAllRecommendedProviderChoices()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.MailMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.TaskMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.IsCapabilitySelectionMissing.Should().BeFalse();
    }

    [Fact]
    public void ImapProvider_DoesNotOfferProviderTasks()
    {
        var viewModel = CreateViewModel(MailProviderType.IMAP4, SpecialImapProvider.iCloud);

        viewModel.IsTaskProviderModeAvailable.Should().BeFalse();
        viewModel.IsTaskProviderUnavailableHintVisible.Should().BeTrue();
        viewModel.IsTaskLocalRecommended.Should().BeTrue();
        viewModel.TaskMode.Should().Be(AccountCapabilityMode.Local);
        viewModel.IsTaskChoiceLocal.Should().BeTrue();
    }

    [Fact]
    public void CapabilityModes_DriveAccessFlags()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.MailMode = AccountCapabilityMode.Off;
        viewModel.ContactMode = AccountCapabilityMode.Local;

        viewModel.IsMailAccessEnabled.Should().BeFalse();
        viewModel.IsContactAccessEnabled.Should().BeTrue();
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
    public void CapabilityStep_StartsWithRecommendedChoicesSelected()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.IsMailChoiceProvider.Should().BeTrue();
        viewModel.IsCalendarChoiceProvider.Should().BeTrue();
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.IsContactChoiceProvider.Should().BeTrue();
        viewModel.IsTaskChoiceProvider.Should().BeTrue();

        viewModel.IsCalendarProviderRecommended.Should().BeTrue();
        viewModel.IsTaskProviderRecommended.Should().BeTrue();
    }

    [Fact]
    public void ChoosingCapability_StaysOnTheCapabilityStep()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Capabilities;

        viewModel.ChooseCapabilityCommand.Execute("Calendar:Local");

        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);
        viewModel.IsCalendarChoiceLocal.Should().BeTrue();
        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Capabilities);
    }

    [Fact]
    public async Task ContinueFromTheProviderStep_MovesToTheCapabilityStep()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.AccountName = "Personal";

        await viewModel.ContinueCommand.ExecuteAsync(null);

        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Capabilities);
    }

    [Fact]
    public void ProviderStep_RequiresBothAProviderAndAnAccountName()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();

        viewModel.AccountName = "Personal";

        viewModel.ContinueCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ChoosingCapability_IgnoresUnknownTokens()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Capabilities;

        viewModel.ChooseCapabilityCommand.Execute("Calendar");
        viewModel.ChooseCapabilityCommand.Execute("Nonsense:Local");
        viewModel.ChooseCapabilityCommand.Execute("Calendar:Nonsense");

        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Capabilities);
    }

    [Fact]
    public void ChangingProvider_RestoresRecommendedCapabilityChoices()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.ChooseCapabilityCommand.Execute("Calendar:Local");
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);

        viewModel.SelectedProvider = new ProviderDetail(MailProviderType.Gmail, SpecialImapProvider.None);

        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
    }
}
