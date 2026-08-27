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
    public void CapabilityScreens_StartWithRecommendedChoicesSelected()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.IsCalendarAnswered.Should().BeTrue();
        viewModel.IsContactAnswered.Should().BeTrue();
        viewModel.IsTaskAnswered.Should().BeTrue();

        viewModel.IsCalendarChoiceProvider.Should().BeTrue();
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.IsContactChoiceProvider.Should().BeTrue();
        viewModel.IsTaskChoiceProvider.Should().BeTrue();
    }

    [Fact]
    public void ChoosingCapability_AnswersTheScreenWithoutMovingToTheNextOne()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Calendar;

        viewModel.ChooseCapabilityCommand.Execute("Calendar:Local");

        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);
        viewModel.IsCalendarAnswered.Should().BeTrue();
        viewModel.IsCalendarChoiceLocal.Should().BeTrue();
        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Calendar);
    }

    [Fact]
    public async Task ContinueAfterChoosingCapability_MovesToTheNextScreen()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Calendar;
        viewModel.ChooseCapabilityCommand.Execute("Calendar:Provider");

        await viewModel.ContinueCommand.ExecuteAsync(null);

        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Contacts);
    }

    [Fact]
    public void ChoosingCapability_IgnoresUnknownTokens()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Calendar;

        viewModel.ChooseCapabilityCommand.Execute("Calendar");
        viewModel.ChooseCapabilityCommand.Execute("Nonsense:Local");
        viewModel.ChooseCapabilityCommand.Execute("Calendar:Nonsense");

        viewModel.IsCalendarAnswered.Should().BeTrue();
        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Calendar);
    }

    [Fact]
    public void SkippingTheRest_PreservesRecommendedChoicesAndJumpsToSummary()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Calendar;

        viewModel.ChooseCapabilityCommand.Execute("Calendar:Provider");
        viewModel.SkipRemainingCapabilitiesCommand.Execute(null);

        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.TaskMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.IsContactAnswered.Should().BeTrue();
        viewModel.IsTaskAnswered.Should().BeTrue();
        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Summary);
    }

    [Fact]
    public void SummaryChangeLink_ReturnsToASingleCapabilityScreen()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Summary;

        viewModel.GoToStepCommand.Execute("Contacts");

        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Contacts);
    }

    [Fact]
    public void ChangingProvider_RestoresRecommendedCapabilityChoices()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.ChooseCapabilityCommand.Execute("Calendar:Local");
        viewModel.IsCalendarAnswered.Should().BeTrue();

        viewModel.SelectedProvider = new ProviderDetail(MailProviderType.Gmail, SpecialImapProvider.None);

        viewModel.IsCalendarAnswered.Should().BeTrue();
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
    }
}
