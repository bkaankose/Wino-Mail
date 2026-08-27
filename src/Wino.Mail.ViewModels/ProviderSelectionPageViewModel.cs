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
        : [Translator.ProviderSelection_SourceCardDav, Translator.ProviderSelection_SourceLocalContacts];

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

    #region Capability modes

    [ObservableProperty]
    public partial AccountCapabilityMode MailMode { get; set; } = AccountCapabilityMode.Provider;

    [ObservableProperty]
    public partial AccountCapabilityMode CalendarMode { get; set; }

    [ObservableProperty]
    public partial AccountCapabilityMode ContactMode { get; set; }

    [ObservableProperty]
    public partial AccountCapabilityMode TaskMode { get; set; }

    // Segmented.SelectedIndex is an int and the segment order matches AccountCapabilityMode,
    // so these are plain passthroughs. A -1 arrives while the control is rebuilding its items.
    public int MailModeIndex
    {
        get => (int)MailMode;
        set { if (value >= 0) MailMode = (AccountCapabilityMode)value; }
    }

    public int CalendarModeIndex
    {
        get => (int)CalendarMode;
        set { if (value >= 0) CalendarMode = (AccountCapabilityMode)value; }
    }

    public int ContactModeIndex
    {
        get => (int)ContactMode;
        set { if (value >= 0) ContactMode = (AccountCapabilityMode)value; }
    }

    public int TaskModeIndex
    {
        get => (int)TaskMode;
        set { if (value >= 0) TaskMode = (AccountCapabilityMode)value; }
    }

    // The old boolean surface is kept so WelcomeWizardContext and callers stay unchanged.
    public bool IsMailAccessEnabled => MailMode != AccountCapabilityMode.Off;
    public bool IsCalendarAccessEnabled => CalendarMode != AccountCapabilityMode.Off;
    public bool IsContactAccessEnabled => ContactMode != AccountCapabilityMode.Off;
    public bool IsTaskAccessEnabled => TaskMode != AccountCapabilityMode.Off;

    #endregion

    #region Capability tiles

    public bool IsMailProviderModeAvailable => true;
    public bool IsCalendarProviderModeAvailable => true;
    public bool IsContactProviderModeAvailable => true;
    public bool IsTaskProviderModeAvailable => IsOAuthProvider;

    public string MailProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_ModeImap;

    public string CalendarProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_SourceCalDav;

    public string ContactProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_SourceCardDav;

    public string TaskProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_ModeImap;

    public string MailStateText => GetStateText(MailMode, MailProviderModeLabel);
    public string CalendarStateText => GetStateText(CalendarMode, CalendarProviderModeLabel);
    public string ContactStateText => GetStateText(ContactMode, ContactProviderModeLabel);
    public string TaskStateText => GetStateText(TaskMode, TaskProviderModeLabel);

    public string MailConsequenceText => MailMode == AccountCapabilityMode.Provider
        ? string.Format(Translator.ProviderSelection_Why_MailProvider, MailProviderModeLabel)
        : string.Empty;

    public string CalendarConsequenceText => CalendarMode switch
    {
        AccountCapabilityMode.Provider => string.Format(Translator.ProviderSelection_Why_CalendarProvider, CalendarProviderModeLabel),
        AccountCapabilityMode.Local => Translator.ProviderSelection_Why_CalendarLocal,
        _ => string.Empty
    };

    public string ContactConsequenceText => ContactMode switch
    {
        AccountCapabilityMode.Provider => string.Format(Translator.ProviderSelection_Why_ContactsProvider, ContactProviderModeLabel),
        AccountCapabilityMode.Local => Translator.ProviderSelection_Why_ContactsLocal,
        _ => string.Empty
    };

    public string TaskConsequenceText => TaskMode switch
    {
        AccountCapabilityMode.Provider => string.Format(Translator.ProviderSelection_Why_TasksProvider, TaskProviderModeLabel),
        AccountCapabilityMode.Local => Translator.ProviderSelection_Why_TasksLocal,
        _ => string.Empty
    };

    public bool IsMailConsequenceVisible => !string.IsNullOrEmpty(MailConsequenceText);
    public bool IsCalendarConsequenceVisible => !string.IsNullOrEmpty(CalendarConsequenceText);
    public bool IsContactConsequenceVisible => !string.IsNullOrEmpty(ContactConsequenceText);
    public bool IsTaskConsequenceVisible => !string.IsNullOrEmpty(TaskConsequenceText);

    public bool IsMailModeSyncedToProvider => MailMode == AccountCapabilityMode.Provider;
    public bool IsCalendarModeSyncedToProvider => CalendarMode == AccountCapabilityMode.Provider;
    public bool IsContactModeSyncedToProvider => ContactMode == AccountCapabilityMode.Provider;
    public bool IsTaskModeSyncedToProvider => TaskMode == AccountCapabilityMode.Provider;

    public bool IsCardDavDiscoveryHintVisible =>
        IsImapFamily && ContactMode == AccountCapabilityMode.Provider;

    public bool IsTaskProviderUnavailableHintVisible => !IsTaskProviderModeAvailable;

    #endregion

    #region Permission summary

    public List<string> ProviderPermissionScopes
    {
        get
        {
            var scopes = new List<string>();

            if (MailMode == AccountCapabilityMode.Provider) scopes.Add(Translator.ProviderSelection_Scope_Mail);
            if (CalendarMode == AccountCapabilityMode.Provider) scopes.Add(Translator.ProviderSelection_Scope_Calendar);
            if (ContactMode == AccountCapabilityMode.Provider) scopes.Add(Translator.ProviderSelection_Scope_Contacts);
            if (TaskMode == AccountCapabilityMode.Provider) scopes.Add(Translator.ProviderSelection_Scope_Tasks);

            return scopes;
        }
    }

    public List<string> LocalOnlyCapabilities
    {
        get
        {
            var local = new List<string>();

            if (CalendarMode == AccountCapabilityMode.Local) local.Add(Translator.ProviderSelection_UseForCalendar);
            if (ContactMode == AccountCapabilityMode.Local) local.Add(Translator.ProviderSelection_UseForContacts);
            if (TaskMode == AccountCapabilityMode.Local) local.Add(Translator.ProviderSelection_UseForTasks);

            return local;
        }
    }

    public string PermissionSummaryHeader => ProviderPermissionScopes.Count > 0
        ? string.Format(Translator.ProviderSelection_PermissionSummary_Header, SelectedProviderName)
        : string.Format(Translator.ProviderSelection_PermissionSummary_None, SelectedProviderName);

    public bool IsLocalOnlySummaryVisible => LocalOnlyCapabilities.Count > 0;

    public string LocalOnlySummaryText => IsLocalOnlySummaryVisible
        ? $"{Translator.ProviderSelection_PermissionSummary_LocalOnly} {string.Join(", ", LocalOnlyCapabilities)}"
        : string.Empty;

    #endregion

    public bool IsColorSelected => SelectedColor != null;
    public bool IsInitialSynchronizationWarningVisible => IsMailSynchronizationRangeVisible && SelectedInitialSynchronizationRange?.IsEverything == true;
    public bool IsMailSynchronizationRangeVisible => IsMailAccessEnabled;

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
    public bool IsDavContactChoiceAvailable => IsImapFamily;
    public bool IsFixedLocalTaskSource => IsImapFamily;
    public string DavContactAvailabilityMessage => Translator.ProviderSelection_CardDavSetupGuidance;
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

            MailMode = WizardContext.IsMailAccessEnabled ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
            CalendarMode = ToCapabilityMode(WizardContext.IsCalendarAccessEnabled, WizardContext.CalendarIntegrationSource);
            ContactMode = ToCapabilityMode(WizardContext.IsContactAccessEnabled, WizardContext.ContactIntegrationSource);
            TaskMode = ToCapabilityMode(WizardContext.IsTaskAccessEnabled, WizardContext.TaskIntegrationSource);

            CoerceUnavailableModes();

            if (WizardContext.AccountColorHex != null)
                SelectedColor = AvailableColors.FirstOrDefault(c => c.Hex == WizardContext.AccountColorHex);
        }
        else
        {
            ApplyProviderCapabilityDefaults();
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
        OnPropertyChanged(nameof(IsOAuthProvider));
        OnPropertyChanged(nameof(IsImapFamily));
        OnPropertyChanged(nameof(IsDavContactChoiceAvailable));
        OnPropertyChanged(nameof(IsFixedLocalTaskSource));
        OnPropertyChanged(nameof(DavContactAvailabilityMessage));
        OnPropertyChanged(nameof(CalendarSourceOptions));
        OnPropertyChanged(nameof(ContactSourceOptions));
        OnPropertyChanged(nameof(TaskSourceOptions));

        ApplyProviderCapabilityDefaults();

        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnAccountNameChanged(string value) => ContinueCommand.NotifyCanExecuteChanged();

    partial void OnMailModeChanged(AccountCapabilityMode value)
    {
        OnPropertyChanged(nameof(MailModeIndex));
        OnPropertyChanged(nameof(IsMailAccessEnabled));
        OnPropertyChanged(nameof(IsMailSynchronizationRangeVisible));
        OnPropertyChanged(nameof(IsInitialSynchronizationWarningVisible));
        OnPropertyChanged(nameof(MailStateText));
        OnPropertyChanged(nameof(MailConsequenceText));
        OnPropertyChanged(nameof(IsMailConsequenceVisible));
        OnPropertyChanged(nameof(IsMailModeSyncedToProvider));
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        NotifyCapabilitySelectionChanged();
    }

    partial void OnCalendarModeChanged(AccountCapabilityMode value)
    {
        OnPropertyChanged(nameof(CalendarModeIndex));
        OnPropertyChanged(nameof(IsCalendarAccessEnabled));
        OnPropertyChanged(nameof(CalendarStateText));
        OnPropertyChanged(nameof(CalendarConsequenceText));
        OnPropertyChanged(nameof(IsCalendarConsequenceVisible));
        OnPropertyChanged(nameof(IsCalendarModeSyncedToProvider));
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        NotifyCapabilitySelectionChanged();
    }

    partial void OnContactModeChanged(AccountCapabilityMode value)
    {
        OnPropertyChanged(nameof(ContactModeIndex));
        OnPropertyChanged(nameof(IsContactAccessEnabled));
        OnPropertyChanged(nameof(ContactStateText));
        OnPropertyChanged(nameof(ContactConsequenceText));
        OnPropertyChanged(nameof(IsContactConsequenceVisible));
        OnPropertyChanged(nameof(IsContactModeSyncedToProvider));
        OnPropertyChanged(nameof(IsCardDavDiscoveryHintVisible));
        NotifyCapabilitySelectionChanged();
    }

    partial void OnTaskModeChanged(AccountCapabilityMode value)
    {
        OnPropertyChanged(nameof(TaskModeIndex));
        OnPropertyChanged(nameof(IsTaskAccessEnabled));
        OnPropertyChanged(nameof(TaskStateText));
        OnPropertyChanged(nameof(TaskConsequenceText));
        OnPropertyChanged(nameof(IsTaskConsequenceVisible));
        OnPropertyChanged(nameof(IsTaskModeSyncedToProvider));
        NotifyCapabilitySelectionChanged();
    }

    private void NotifyCapabilitySelectionChanged()
    {
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        OnPropertyChanged(nameof(ProviderPermissionScopes));
        OnPropertyChanged(nameof(LocalOnlyCapabilities));
        OnPropertyChanged(nameof(PermissionSummaryHeader));
        OnPropertyChanged(nameof(IsLocalOnlySummaryVisible));
        OnPropertyChanged(nameof(LocalOnlySummaryText));
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
        WizardContext.CalendarIntegrationSource = ToIntegrationSource(CalendarMode);
        WizardContext.ContactIntegrationSource = ToIntegrationSource(ContactMode);
        WizardContext.TaskIntegrationSource = IsImapFamily
            ? AccountIntegrationSource.Local
            : ToIntegrationSource(TaskMode);
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

    private static string GetStateText(AccountCapabilityMode mode, string providerLabel) => mode switch
    {
        AccountCapabilityMode.Provider => string.Format(Translator.ProviderSelection_State_Synced, providerLabel),
        AccountCapabilityMode.Local => Translator.ProviderSelection_State_Local,
        _ => Translator.ProviderSelection_State_Off
    };

    private static AccountCapabilityMode ToCapabilityMode(bool isEnabled, AccountIntegrationSource source)
    {
        if (!isEnabled) return AccountCapabilityMode.Off;

        return source == AccountIntegrationSource.Local
            ? AccountCapabilityMode.Local
            : AccountCapabilityMode.Provider;
    }

    private AccountIntegrationSource ToIntegrationSource(AccountCapabilityMode mode)
    {
        if (mode == AccountCapabilityMode.Local) return AccountIntegrationSource.Local;

        return IsOAuthProvider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Dav;
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

    private void ApplyProviderCapabilityDefaults()
    {
        MailMode = AccountCapabilityMode.Provider;
        CalendarMode = AccountCapabilityMode.Off;
        ContactMode = IsOAuthProvider ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
        TaskMode = AccountCapabilityMode.Off;

        CoerceUnavailableModes();
    }

    /// <summary>
    /// Falls back to the local mode when the selected provider cannot serve a capability,
    /// so a disabled segment is never the active one.
    /// </summary>
    private void CoerceUnavailableModes()
    {
        if (!IsContactProviderModeAvailable && ContactMode == AccountCapabilityMode.Provider)
            ContactMode = AccountCapabilityMode.Local;

        if (!IsTaskProviderModeAvailable && TaskMode == AccountCapabilityMode.Provider)
            TaskMode = AccountCapabilityMode.Local;

        OnPropertyChanged(nameof(IsContactProviderModeAvailable));
        OnPropertyChanged(nameof(IsTaskProviderModeAvailable));
        OnPropertyChanged(nameof(IsTaskProviderUnavailableHintVisible));
        OnPropertyChanged(nameof(IsCardDavDiscoveryHintVisible));
        OnPropertyChanged(nameof(MailProviderModeLabel));
        OnPropertyChanged(nameof(CalendarProviderModeLabel));
        OnPropertyChanged(nameof(ContactProviderModeLabel));
        OnPropertyChanged(nameof(TaskProviderModeLabel));
        OnPropertyChanged(nameof(MailStateText));
        OnPropertyChanged(nameof(CalendarStateText));
        OnPropertyChanged(nameof(ContactStateText));
        OnPropertyChanged(nameof(TaskStateText));
        OnPropertyChanged(nameof(MailConsequenceText));
        OnPropertyChanged(nameof(CalendarConsequenceText));
        OnPropertyChanged(nameof(ContactConsequenceText));
        OnPropertyChanged(nameof(TaskConsequenceText));
        NotifyCapabilitySelectionChanged();
    }
}
