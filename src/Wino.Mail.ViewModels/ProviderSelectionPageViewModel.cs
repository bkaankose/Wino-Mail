using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public enum ProviderSelectionWizardStep
{
    Provider = 0,
    Identity = 1,
    Capabilities = 2
}

public partial class ProviderSelectionPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IDialogServiceBase _dialogService;
    private readonly IProviderService _providerService;
    private readonly INewThemeService _themeService;
    private ProviderSelectionHostMode _hostMode = ProviderSelectionHostMode.Wizard;

    public WelcomeWizardContext WizardContext { get; }

    public List<IProviderDetail> Providers { get; private set; } = [];
    public List<AppColorViewModel> AvailableColors { get; private set; } = [];
    public List<InitialSynchronizationRangeOption> InitialSynchronizationRanges { get; } =
    [
        new(InitialSynchronizationRange.ThreeMonths, Translator.AccountCreation_InitialSynchronization_3Months),
        new(InitialSynchronizationRange.SixMonths, Translator.AccountCreation_InitialSynchronization_6Months),
        new(InitialSynchronizationRange.NineMonths, Translator.AccountCreation_InitialSynchronization_9Months),
        new(InitialSynchronizationRange.OneYear, Translator.AccountCreation_InitialSynchronization_Year),
        new(InitialSynchronizationRange.Everything, Translator.AccountCreation_InitialSynchronization_Everything)
    ];

    public List<string> CalendarSourceOptions => IsOAuthProvider
        ? [Translator.ProviderSelection_SourceProviderCalendar, Translator.ProviderSelection_SourceLocalCalendar]
        : [Translator.ProviderSelection_SourceCalDav, Translator.ProviderSelection_SourceLocalCalendar];

    public List<string> ContactSourceOptions => IsOAuthProvider
        ? [Translator.ProviderSelection_SourceProviderContacts, Translator.ProviderSelection_SourceLocalContacts]
        : [Translator.ProviderSelection_SourceLocalContacts];

    public List<string> TaskSourceOptions => IsOAuthProvider
        ? [Translator.ProviderSelection_SourceProviderTasks, Translator.ProviderSelection_SourceLocalTasks]
        : [Translator.ProviderSelection_SourceLocalTasks];

    [ObservableProperty]
    public partial IProviderDetail SelectedProvider { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColorSelected))]
    public partial AppColorViewModel SelectedColor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitialSynchronizationWarningVisible))]
    [NotifyPropertyChangedFor(nameof(IsMailSynchronizationRangeVisible))]
    public partial InitialSynchronizationRangeOption SelectedInitialSynchronizationRange { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMailSynchronizationRangeVisible))]
    public partial bool IsMailAccessEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCalendarAccessEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsContactAccessEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsTaskAccessEnabled { get; set; }

    [ObservableProperty]
    public partial int SelectedCalendarSourceIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedContactSourceIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedTaskSourceIndex { get; set; }

    [ObservableProperty]
    public partial bool IsMailExpanderExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCalendarExpanderExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsContactExpanderExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsTaskExpanderExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(StepProgressValue))]
    [NotifyPropertyChangedFor(nameof(StepProgressText))]
    [NotifyPropertyChangedFor(nameof(IsProviderStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsIdentityStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsCapabilityStepVisible))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial ProviderSelectionWizardStep CurrentStep { get; set; } = ProviderSelectionWizardStep.Provider;

    public bool IsColorSelected => SelectedColor != null;
    public bool IsInitialSynchronizationWarningVisible => IsMailSynchronizationRangeVisible && SelectedInitialSynchronizationRange?.IsEverything == true;
    public bool IsMailSynchronizationRangeVisible => IsMailAccessEnabled;
    public int CurrentStepNumber => (int)CurrentStep + 1;
    public double StepProgressValue => CurrentStepNumber;
    public string StepProgressText => string.Format(Translator.ProviderSelection_StepProgress, CurrentStepNumber);
    public bool IsProviderStepVisible => CurrentStep == ProviderSelectionWizardStep.Provider;
    public bool IsIdentityStepVisible => CurrentStep == ProviderSelectionWizardStep.Identity;
    public bool IsCapabilityStepVisible => CurrentStep == ProviderSelectionWizardStep.Capabilities;
    public bool CanGoBack => CurrentStep != ProviderSelectionWizardStep.Provider;
    public string SelectedProviderName => SelectedProvider?.Name ?? string.Empty;
    public string SelectedProviderDescription => SelectedProvider?.Description ?? string.Empty;
    public string SelectedProviderImage => SelectedProvider?.ProviderImage ?? string.Empty;
    public string SelectedProviderCapabilityDescription => GetSelectedProviderCapabilityDescription();
    public bool IsOAuthProvider => SelectedProvider?.Type is MailProviderType.Outlook or MailProviderType.Gmail;
    public bool IsImapFamily => SelectedProvider?.Type == MailProviderType.IMAP4;
    public bool IsDavContactChoiceAvailable => false;
    public bool IsFixedLocalTaskSource => IsImapFamily;
    public string DavContactAvailabilityMessage => SelectedProvider?.SpecialImapProvider == SpecialImapProvider.iCloud
        ? Translator.ProviderSelection_CardDavUnavailableICloud
        : Translator.ProviderSelection_CardDavUnavailableManual;
    public bool IsCapabilitySelectionMissing => !IsMailAccessEnabled && !IsCalendarAccessEnabled &&
        !IsContactAccessEnabled && !IsTaskAccessEnabled;
    public bool IsCalendarOnlyServerHintVisible =>
        SelectedProvider?.Type == MailProviderType.IMAP4 &&
        !IsMailAccessEnabled &&
        IsCalendarAccessEnabled;

    public ProviderSelectionPageViewModel(
        IAccountService accountService,
        IDialogServiceBase dialogService,
        IProviderService providerService,
        INewThemeService themeService,
        WelcomeWizardContext wizardContext)
    {
        _accountService = accountService;
        _dialogService = dialogService;
        _providerService = providerService;
        _themeService = themeService;
        WizardContext = wizardContext;
        SelectedInitialSynchronizationRange = InitialSynchronizationRanges.First(option => option.Range == InitialSynchronizationRange.SixMonths);
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        var navigationContext = parameters as ProviderSelectionNavigationContext
                                ?? ProviderSelectionNavigationContext.CreateForWizard();

        _hostMode = navigationContext.HostMode;

        if (mode != NavigationMode.Back)
        {
            WizardContext.Reset();
        }

        Providers = _providerService.GetAvailableProviders();
        AvailableColors = _themeService.GetAvailableAccountColors()
            .Select(hex => new AppColorViewModel(hex))
            .ToList();

        SelectedInitialSynchronizationRange = InitialSynchronizationRanges
            .FirstOrDefault(option => option.Range == WizardContext.SelectedInitialSynchronizationRange)
            ?? InitialSynchronizationRanges.First(option => option.Range == InitialSynchronizationRange.SixMonths);

        // Restore from wizard context if navigating back
        if (WizardContext.SelectedProvider != null)
        {
            SelectedProvider = Providers.FirstOrDefault(p =>
                p.Type == WizardContext.SelectedProvider.Type &&
                p.SpecialImapProvider == WizardContext.SelectedProvider.SpecialImapProvider);
            AccountName = WizardContext.AccountName;
            IsMailAccessEnabled = WizardContext.IsMailAccessEnabled;
            IsCalendarAccessEnabled = WizardContext.IsCalendarAccessEnabled;
            IsContactAccessEnabled = WizardContext.IsContactAccessEnabled;
            IsTaskAccessEnabled = WizardContext.IsTaskAccessEnabled;
            SelectedCalendarSourceIndex = WizardContext.CalendarIntegrationSource == AccountIntegrationSource.Local ? 1 : 0;
            SelectedContactSourceIndex = WizardContext.ContactIntegrationSource == AccountIntegrationSource.Local && IsOAuthProvider ? 1 : 0;
            SelectedTaskSourceIndex = WizardContext.TaskIntegrationSource == AccountIntegrationSource.Local && IsOAuthProvider ? 1 : 0;

            if (WizardContext.AccountColorHex != null)
                SelectedColor = AvailableColors.FirstOrDefault(c => c.Hex == WizardContext.AccountColorHex);
        }
        else
        {
            IsMailAccessEnabled = true;
            IsCalendarAccessEnabled = false;
            IsContactAccessEnabled = IsOAuthProvider;
            IsTaskAccessEnabled = false;
            ApplyProviderSourceDefaults();
        }

        CurrentStep = mode == NavigationMode.Back && SelectedProvider != null
            ? ProviderSelectionWizardStep.Capabilities
            : ProviderSelectionWizardStep.Provider;
    }

    partial void OnSelectedProviderChanged(IProviderDetail value)
    {
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedProviderDescription));
        OnPropertyChanged(nameof(SelectedProviderImage));
        OnPropertyChanged(nameof(SelectedProviderCapabilityDescription));
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        OnPropertyChanged(nameof(IsOAuthProvider));
        OnPropertyChanged(nameof(IsImapFamily));
        OnPropertyChanged(nameof(IsDavContactChoiceAvailable));
        OnPropertyChanged(nameof(IsFixedLocalTaskSource));
        OnPropertyChanged(nameof(DavContactAvailabilityMessage));
        OnPropertyChanged(nameof(CalendarSourceOptions));
        OnPropertyChanged(nameof(ContactSourceOptions));
        OnPropertyChanged(nameof(TaskSourceOptions));
        IsContactAccessEnabled = IsOAuthProvider;
        IsTaskAccessEnabled = false;
        ApplyProviderSourceDefaults();
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnAccountNameChanged(string value) => ContinueCommand.NotifyCanExecuteChanged();

    partial void OnIsMailAccessEnabledChanged(bool value)
    {
        IsMailExpanderExpanded = value;
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCalendarAccessEnabledChanged(bool value)
    {
        IsCalendarExpanderExpanded = value;
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsContactAccessEnabledChanged(bool value)
    {
        IsContactExpanderExpanded = value;
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTaskAccessEnabledChanged(bool value)
    {
        IsTaskExpanderExpanded = value;
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearColor() => SelectedColor = null;

    private bool CanContinue()
    {
        return CurrentStep switch
        {
            ProviderSelectionWizardStep.Provider => SelectedProvider != null,
            ProviderSelectionWizardStep.Identity => !string.IsNullOrWhiteSpace(AccountName),
            ProviderSelectionWizardStep.Capabilities => !IsCapabilitySelectionMissing,
            _ => false
        };
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (!CanGoBack)
            return;

        CurrentStep--;
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        switch (CurrentStep)
        {
            case ProviderSelectionWizardStep.Provider:
                CurrentStep = ProviderSelectionWizardStep.Identity;
                return;
            case ProviderSelectionWizardStep.Identity:
                if (await _accountService.AccountNameExistsAsync(AccountName?.Trim()))
                {
                    await _dialogService.ShowMessageAsync(
                        Translator.DialogMessage_AccountNameExistsMessage,
                        Translator.DialogMessage_AccountExistsTitle,
                        WinoCustomMessageDialogIcon.Warning);
                    return;
                }

                CurrentStep = ProviderSelectionWizardStep.Capabilities;
                return;
            case ProviderSelectionWizardStep.Capabilities:
                await CompleteWizardAsync();
                return;
        }
    }

    private async Task CompleteWizardAsync()
    {
        if (!CanContinue())
            return;

        WizardContext.SelectedProvider = SelectedProvider;
        WizardContext.AccountName = AccountName?.Trim();
        WizardContext.AccountColorHex = SelectedColor?.Hex ?? string.Empty;
        WizardContext.SelectedInitialSynchronizationRange = SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths;
        WizardContext.IsMailAccessEnabled = IsMailAccessEnabled;
        WizardContext.IsCalendarAccessEnabled = IsCalendarAccessEnabled;
        WizardContext.IsContactAccessEnabled = IsContactAccessEnabled;
        WizardContext.IsTaskAccessEnabled = IsTaskAccessEnabled;
        WizardContext.CalendarIntegrationSource = SelectedCalendarSourceIndex == 1
            ? AccountIntegrationSource.Local
            : IsOAuthProvider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Dav;
        WizardContext.ContactIntegrationSource = SelectedContactSourceIndex == 1 || IsImapFamily
            ? AccountIntegrationSource.Local
            : AccountIntegrationSource.Provider;
        WizardContext.TaskIntegrationSource = SelectedTaskSourceIndex == 1 || IsImapFamily
            ? AccountIntegrationSource.Local
            : AccountIntegrationSource.Provider;
        WizardContext.CalendarSupportMode = !IsCalendarAccessEnabled
            ? ImapCalendarSupportMode.Disabled
            : WizardContext.CalendarIntegrationSource == AccountIntegrationSource.Local
                ? ImapCalendarSupportMode.LocalOnly
                : ImapCalendarSupportMode.CalDav;

        if (WizardContext.IsGenericImap)
        {
            var context = _hostMode == ProviderSelectionHostMode.SettingsAddAccount
                ? ImapCalDavSettingsNavigationContext.CreateForAddAccountMode(
                    WizardContext.BuildAccountCreationDialogResult())
                : ImapCalDavSettingsNavigationContext.CreateForWizardMode(
                    WizardContext.BuildAccountCreationDialogResult());

            Messenger.Send(new BreadcrumbNavigationRequested(
                Translator.ImapCalDavSettingsPage_TitleCreate,
                WinoPage.ImapCalDavSettingsPage,
                context));
        }
        else if (SelectedProvider.SpecialImapProvider is SpecialImapProvider.iCloud or SpecialImapProvider.Yahoo)
        {
            // Navigate to credentials page for special IMAP providers
            Messenger.Send(new BreadcrumbNavigationRequested(
                SelectedProvider.Name,
                WinoPage.SpecialImapCredentialsPage));
        }
        else
        {
            // OAuth — go directly to progress page
            Messenger.Send(new BreadcrumbNavigationRequested(
                Translator.WelcomeWizard_Step3Title,
                WinoPage.AccountSetupProgressPage));
        }
    }

    partial void OnSelectedProviderChanging(IProviderDetail value)
    {

    }

    private string GetSelectedProviderCapabilityDescription()
    {
        if (SelectedProvider == null)
            return string.Empty;

        if (SelectedProvider.Type is MailProviderType.Outlook or MailProviderType.Gmail)
            return Translator.ProviderSelection_CapabilityProviderDescription_OAuth;

        if (SelectedProvider.SpecialImapProvider is SpecialImapProvider.iCloud or SpecialImapProvider.Yahoo)
            return Translator.ProviderSelection_CapabilityProviderDescription_SpecialImap;

        return Translator.ProviderSelection_CapabilityProviderDescription_CustomServer;
    }

    private void ApplyProviderSourceDefaults()
    {
        SelectedCalendarSourceIndex = IsOAuthProvider || SelectedProvider?.SpecialImapProvider is SpecialImapProvider.iCloud or SpecialImapProvider.Yahoo ? 0 : 1;
        SelectedContactSourceIndex = 0;
        SelectedTaskSourceIndex = 0;
    }
}
