using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ProviderSelectionPageViewModelTests
{
    [Fact]
    public void AccountSetupProviders_ExcludePop3()
    {
        var providerService = new Mock<IKnownImapProviderCatalog>();
        providerService.Setup(service => service.GetAvailableProviders()).Returns(
        [
            new ProviderDetail(MailProviderType.Outlook, SpecialImapProvider.None),
            new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.None),
            new ProviderDetail(MailProviderType.POP3, SpecialImapProvider.None)
        ]);
        var themeService = new Mock<INewThemeService>();
        themeService.Setup(service => service.GetAvailableAccountColors()).Returns([]);
        var viewModel = new ProviderSelectionPageViewModel(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            providerService.Object,
            themeService.Object,
            new WelcomeWizardContext());

        viewModel.OnNavigatedTo(NavigationMode.New, ProviderSelectionNavigationContext.CreateForWizard());

        viewModel.Providers.Should().NotContain(provider => provider.Type == MailProviderType.POP3);
        viewModel.Providers.Should().Contain(provider => provider.Type == MailProviderType.IMAP4);
    }

    [Fact]
    public void ICloudContactSources_IncludeCardDavAndLocal()
    {
        var viewModel = new ProviderSelectionPageViewModel(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IKnownImapProviderCatalog>(),
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
            Mock.Of<IKnownImapProviderCatalog>(),
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
    public async Task ContinueFromTheProviderStep_MovesToTheIdentityStep()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        await viewModel.ContinueCommand.ExecuteAsync(null);

        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Identity);
        viewModel.IsIdentityStepVisible.Should().BeTrue();
        viewModel.IsAccountSummaryVisible.Should().BeTrue();
    }

    [Fact]
    public async Task ContinueFromTheIdentityStep_MovesToTheCapabilityStep()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        await viewModel.ContinueCommand.ExecuteAsync(null);
        viewModel.AccountName = "Personal";
        await viewModel.ContinueCommand.ExecuteAsync(null);

        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Capabilities);
    }

    [Fact]
    public void ProviderStep_RequiresOnlyASelectedProvider()
    {
        var viewModel = new ProviderSelectionPageViewModel(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IKnownImapProviderCatalog>(),
            Mock.Of<INewThemeService>(),
            new WelcomeWizardContext());

        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();

        viewModel.SelectedProvider = new ProviderDetail(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.ContinueCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void IdentityStep_RequiresAnAccountName()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Identity;

        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();

        viewModel.AccountName = "   ";
        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();

        viewModel.AccountName = "Personal";
        viewModel.ContinueCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void IdentityStep_GoesBackToTheProviderStep()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Identity;

        viewModel.GoBackCommand.Execute(null);

        viewModel.CurrentStep.Should().Be(ProviderSelectionWizardStep.Provider);
        viewModel.CanGoBack.Should().BeFalse();
        viewModel.IsAccountSummaryVisible.Should().BeFalse();
    }

    [Fact]
    public void SummaryDetail_FallsBackToTheProviderDescriptionUntilTheAccountIsNamed()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        viewModel.SelectedProviderSummaryDetail.Should().Be(viewModel.SelectedProviderDescription);

        viewModel.AccountName = "Personal";

        viewModel.SelectedProviderSummaryDetail.Should().Be("Personal");
    }

    [Fact]
    public void StepProgress_CountsThreeSteps()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook, SpecialImapProvider.None);

        ProviderSelectionPageViewModel.TotalStepCount.Should().Be(3);
        viewModel.CurrentStepNumber.Should().Be(1);

        viewModel.CurrentStep = ProviderSelectionWizardStep.Capabilities;

        viewModel.CurrentStepNumber.Should().Be(3);
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
    public async Task CustomServerWithOnlyLocalCapabilities_SkipsServerPageWithLocalSetupResult()
    {
        var wizardContext = new WelcomeWizardContext();
        var viewModel = new ProviderSelectionPageViewModel(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IKnownImapProviderCatalog>(),
            Mock.Of<INewThemeService>(),
            wizardContext)
        {
            SelectedProvider = new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.None),
            AccountName = "Tasks only"
        };
        viewModel.CurrentStep = ProviderSelectionWizardStep.Capabilities;

        viewModel.MailMode = AccountCapabilityMode.Off;
        viewModel.CalendarMode = AccountCapabilityMode.Off;
        viewModel.ContactMode = AccountCapabilityMode.Off;
        viewModel.TaskMode = AccountCapabilityMode.Local;

        viewModel.IsLocalOnlyHintVisible.Should().BeTrue();
        viewModel.ContinueButtonText.Should().Be(Translator.ProviderSelection_AddAccountButton);

        await viewModel.ContinueCommand.ExecuteAsync(null);

        var result = wizardContext.ImapCalDavSetupResult;
        result.Should().NotBeNull();
        result!.IsMailAccessGranted.Should().BeFalse();
        result.IsCalendarAccessGranted.Should().BeFalse();
        result.EmailAddress.Should().BeEmpty();
        result.ServerInformation.CalendarSupportMode.Should().Be(ImapCalendarSupportMode.Disabled);
        result.ServerInformation.IncomingServer.Should().BeNullOrEmpty();
        wizardContext.IsTaskAccessEnabled.Should().BeTrue();
        wizardContext.TaskIntegrationSource.Should().Be(AccountIntegrationSource.Local);
    }

    [Fact]
    public async Task CustomServerWithLocalCalendar_KeepsLocalCalendarInSetupResult()
    {
        var wizardContext = new WelcomeWizardContext();
        var viewModel = new ProviderSelectionPageViewModel(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IKnownImapProviderCatalog>(),
            Mock.Of<INewThemeService>(),
            wizardContext)
        {
            SelectedProvider = new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.None),
            AccountName = "Family"
        };
        viewModel.CurrentStep = ProviderSelectionWizardStep.Capabilities;

        viewModel.MailMode = AccountCapabilityMode.Off;
        viewModel.CalendarMode = AccountCapabilityMode.Local;
        viewModel.ContactMode = AccountCapabilityMode.Local;

        await viewModel.ContinueCommand.ExecuteAsync(null);

        wizardContext.ImapCalDavSetupResult!.IsCalendarAccessGranted.Should().BeTrue();
        wizardContext.ImapCalDavSetupResult.ServerInformation.CalendarSupportMode.Should().Be(ImapCalendarSupportMode.LocalOnly);
    }

    [Fact]
    public void CustomServerWithServerCapability_ContinuesToSignIn()
    {
        var viewModel = CreateViewModel(MailProviderType.IMAP4, SpecialImapProvider.None);
        viewModel.CurrentStep = ProviderSelectionWizardStep.Capabilities;

        viewModel.MailMode = AccountCapabilityMode.Off;
        viewModel.CalendarMode = AccountCapabilityMode.Provider;

        viewModel.IsLocalOnlyHintVisible.Should().BeFalse();
        viewModel.ContinueButtonText.Should().Be(Translator.ProviderSelection_ContinueButton);
    }

    [Fact]
    public void CapabilityPickerChanges_ReachTheWizardModes()
    {
        var viewModel = CreateViewModel(MailProviderType.IMAP4, SpecialImapProvider.None);

        viewModel.Capabilities.IsCalendarLocalSelected = true;
        viewModel.Capabilities.IsMailEnabled = false;

        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);
        viewModel.IsMailAccessEnabled.Should().BeFalse();
        viewModel.IsCalendarChoiceLocal.Should().BeTrue();
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
