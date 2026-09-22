using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
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

    /// <summary>
    /// The capability choices, shared with the AccountCapabilityPicker control.
    /// </summary>
    public AccountCapabilitySelection Capabilities { get; } = new();

    public AccountCapabilityMode MailMode
    {
        get => Capabilities.MailMode;
        set => Capabilities.MailMode = value;
    }

    public AccountCapabilityMode CalendarMode
    {
        get => Capabilities.CalendarMode;
        set => Capabilities.CalendarMode = value;
    }

    public AccountCapabilityMode ContactMode
    {
        get => Capabilities.ContactMode;
        set => Capabilities.ContactMode = value;
    }

    public AccountCapabilityMode TaskMode
    {
        get => Capabilities.TaskMode;
        set => Capabilities.TaskMode = value;
    }

    // The old boolean surface is kept so WelcomeWizardContext and callers stay unchanged.
    public bool IsMailAccessEnabled => MailMode != AccountCapabilityMode.Off;
    public bool IsCalendarAccessEnabled => CalendarMode != AccountCapabilityMode.Off;
    public bool IsContactAccessEnabled => ContactMode != AccountCapabilityMode.Off;
    public bool IsTaskAccessEnabled => TaskMode != AccountCapabilityMode.Off;

    #endregion

    #region Capability tiles

    public bool IsMailProviderModeAvailable => true;
    public bool IsCalendarProviderModeAvailable => !IsPop3;
    public bool IsContactProviderModeAvailable => !IsPop3;
    public bool IsTaskProviderModeAvailable => IsOAuthProvider;

    public string MailProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : IsPop3 ? Translator.ProviderSelection_ModePop3 : Translator.ProviderSelection_ModeImap;

    public string CalendarProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_SourceCalDav;

    public string ContactProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_SourceCardDav;

    public string TaskProviderModeLabel => IsOAuthProvider
        ? SelectedProviderName
        : Translator.ProviderSelection_ModeImap;

    public bool IsCardDavDiscoveryHintVisible =>
        IsImapFamily && ContactMode == AccountCapabilityMode.Provider;

    public bool IsTaskProviderUnavailableHintVisible => !IsTaskProviderModeAvailable;

    #endregion

    public bool IsColorSelected => SelectedColor != null;
    public bool IsInitialSynchronizationWarningVisible => IsMailSynchronizationRangeVisible && SelectedInitialSynchronizationRange?.IsEverything == true;
    public bool IsMailSynchronizationRangeVisible => IsMailAccessEnabled;

    /// <summary>
    /// The provider on its own, then the account identity, then every capability on one screen.
    /// </summary>
    public const int TotalStepCount = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(StepProgressValue))]
    [NotifyPropertyChangedFor(nameof(StepProgressText))]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageSubtitle))]
    [NotifyPropertyChangedFor(nameof(IsProviderStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsIdentityStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsCapabilityStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsAccountSummaryVisible))]
    [NotifyPropertyChangedFor(nameof(ContinueButtonText))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial ProviderSelectionWizardStep CurrentStep { get; set; } = ProviderSelectionWizardStep.Provider;

    public int CurrentStepNumber => (int)CurrentStep + 1;
    public double StepProgressValue => CurrentStepNumber;
    public string StepProgressText => string.Format(Translator.ProviderSelection_StepProgressOf, CurrentStepNumber, TotalStepCount);
    public bool IsProviderStepVisible => CurrentStep == ProviderSelectionWizardStep.Provider;
    public bool IsIdentityStepVisible => CurrentStep == ProviderSelectionWizardStep.Identity;
    public bool IsCapabilityStepVisible => CurrentStep == ProviderSelectionWizardStep.Capabilities;

    /// <summary>
    /// The chosen provider stays on screen after its step, so the later steps keep their context.
    /// </summary>
    public bool IsAccountSummaryVisible => CurrentStep != ProviderSelectionWizardStep.Provider;

    public bool CanGoBack => CurrentStep != ProviderSelectionWizardStep.Provider;

    public string PageTitle => CurrentStep switch
    {
        ProviderSelectionWizardStep.Identity => Translator.ProviderSelection_IdentityTitle,
        ProviderSelectionWizardStep.Capabilities => Translator.ProviderSelection_CapabilityStepTitle,
        _ => Translator.ProviderSelection_Title
    };

    public string PageSubtitle => CurrentStep switch
    {
        ProviderSelectionWizardStep.Identity => Translator.ProviderSelection_IdentityDescription,
        ProviderSelectionWizardStep.Capabilities => Translator.ProviderSelection_CapabilityStepSubtitle,
        _ => Translator.ProviderSelection_Subtitle
    };

    public string ContinueButtonText => IsCapabilityStepVisible && !IsSignInStepNext
        ? Translator.ProviderSelection_AddAccountButton
        : Translator.ProviderSelection_ContinueButton;

    /// <summary>
    /// IMAP-family providers continue to a sign-in page unless every capability stays on this PC.
    /// </summary>
    private bool IsSignInStepNext =>
        (IsImapFamily || IsPop3) &&
        (SelectedProvider?.SpecialImapProvider != SpecialImapProvider.None || Capabilities.RequiresRemoteService);

    #region Capability answers

    // Every capability lives on one screen and starts on its recommended choice, so a mode
    // always has exactly one selected option.
    public bool IsMailChoiceProvider => MailMode == AccountCapabilityMode.Provider;
    public bool IsMailChoiceOff => MailMode == AccountCapabilityMode.Off;
    public int MailModeIndex
    {
        get => MailMode == AccountCapabilityMode.Provider ? 0 : 1;
        set => MailMode = value == 0 ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
    }

    public bool IsCalendarChoiceProvider => CalendarMode == AccountCapabilityMode.Provider;
    public bool IsCalendarChoiceLocal => CalendarMode == AccountCapabilityMode.Local;
    public bool IsCalendarChoiceOff => CalendarMode == AccountCapabilityMode.Off;
    public int CalendarModeIndex
    {
        get => ToCapabilityModeIndex(CalendarMode);
        set => CalendarMode = FromCapabilityModeIndex(value);
    }

    public bool IsContactChoiceProvider => ContactMode == AccountCapabilityMode.Provider;
    public bool IsContactChoiceLocal => ContactMode == AccountCapabilityMode.Local;
    public bool IsContactChoiceOff => ContactMode == AccountCapabilityMode.Off;
    public int ContactModeIndex
    {
        get => ToCapabilityModeIndex(ContactMode);
        set => ContactMode = FromCapabilityModeIndex(value);
    }

    public bool IsTaskChoiceProvider => TaskMode == AccountCapabilityMode.Provider;
    public bool IsTaskChoiceLocal => TaskMode == AccountCapabilityMode.Local;
    public bool IsTaskChoiceOff => TaskMode == AccountCapabilityMode.Off;
    public int TaskModeIndex
    {
        get => ToCapabilityModeIndex(TaskMode);
        set => TaskMode = FromCapabilityModeIndex(value);
    }

    public string MailOffConsequenceText => Translator.ProviderSelection_Why_MailOff;
    public string CalendarOffConsequenceText => Translator.ProviderSelection_Why_CalendarOff;
    public string ContactOffConsequenceText => Translator.ProviderSelection_Why_ContactsOff;
    public string TaskOffConsequenceText => Translator.ProviderSelection_Why_TasksOff;

    public string MailProviderChoiceDescription => string.Format(Translator.ProviderSelection_Why_MailProvider, MailProviderModeLabel);
    public string CalendarProviderChoiceDescription => string.Format(Translator.ProviderSelection_Why_CalendarProvider, CalendarProviderModeLabel);
    public string ContactProviderChoiceDescription => string.Format(Translator.ProviderSelection_Why_ContactsProvider, ContactProviderModeLabel);
    public string TaskProviderChoiceDescription => string.Format(Translator.ProviderSelection_Why_TasksProvider, TaskProviderModeLabel);

    // The recommended option per capability. It is the provider option wherever the provider can
    // serve the capability, and the local option otherwise.
    public bool IsMailProviderRecommended => true;
    public bool IsCalendarProviderRecommended => IsCalendarProviderModeAvailable;
    public bool IsCalendarLocalRecommended => !IsCalendarProviderModeAvailable;
    public bool IsContactProviderRecommended => IsContactProviderModeAvailable;
    public bool IsContactLocalRecommended => !IsContactProviderModeAvailable;
    public bool IsTaskProviderRecommended => IsTaskProviderModeAvailable;
    public bool IsTaskLocalRecommended => !IsTaskProviderModeAvailable;

    #endregion
    public string SelectedProviderName => SelectedProvider?.Name ?? string.Empty;
    public string SelectedProviderDescription => SelectedProvider?.Description ?? string.Empty;
    public string SelectedProviderImage => SelectedProvider?.ProviderImage ?? string.Empty;

    /// <summary>
    /// The account name once it exists, and the provider description until then.
    /// </summary>
    public string SelectedProviderSummaryDetail => string.IsNullOrWhiteSpace(AccountName)
        ? SelectedProviderDescription
        : AccountName;

    public string SelectedProviderCapabilityDescription => GetSelectedProviderCapabilityDescription();
    public bool IsOAuthProvider => SelectedProvider?.Type is MailProviderType.Outlook or MailProviderType.Gmail;
    public bool IsImapFamily => SelectedProvider?.Type == MailProviderType.IMAP4;
    public bool IsPop3 => SelectedProvider?.Type == MailProviderType.POP3;
    public bool IsDavContactChoiceAvailable => IsImapFamily;
    public bool IsFixedLocalTaskSource => IsImapFamily;
    public string DavContactAvailabilityMessage => Translator.ProviderSelection_CardDavSetupGuidance;
    public bool IsCapabilitySelectionMissing => !IsMailAccessEnabled && !IsCalendarAccessEnabled &&
        !IsContactAccessEnabled && !IsTaskAccessEnabled;
    /// <summary>
    /// A custom-server account whose capabilities all stay on this PC skips the server page.
    /// </summary>
    public bool IsLocalOnlyHintVisible =>
        IsImapFamily &&
        SelectedProvider?.SpecialImapProvider == SpecialImapProvider.None &&
        Capabilities.HasAnyCapability &&
        !Capabilities.RequiresRemoteService;

    // Only a CalDAV calendar leads to the server page; a local one skips it.
    public bool IsCalendarOnlyServerHintVisible =>
        SelectedProvider?.Type == MailProviderType.IMAP4 &&
        !IsMailAccessEnabled &&
        CalendarMode == AccountCapabilityMode.Provider;

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

        Capabilities.PropertyChanged += OnCapabilitiesPropertyChanged;
    }

    private void OnCapabilitiesPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AccountCapabilitySelection.MailMode):
                OnPropertyChanged(nameof(MailMode));
                OnMailModeChanged();
                break;
            case nameof(AccountCapabilitySelection.CalendarMode):
                OnPropertyChanged(nameof(CalendarMode));
                OnCalendarModeChanged();
                break;
            case nameof(AccountCapabilitySelection.ContactMode):
                OnPropertyChanged(nameof(ContactMode));
                OnContactModeChanged();
                break;
            case nameof(AccountCapabilitySelection.TaskMode):
                OnPropertyChanged(nameof(TaskMode));
                OnTaskModeChanged();
                break;
        }
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

        Providers = _providerService.GetAvailableProviders()
            .Where(provider => provider.Type != MailProviderType.POP3)
            .ToList();
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
        OnPropertyChanged(nameof(SelectedProviderSummaryDetail));
        OnPropertyChanged(nameof(SelectedProviderCapabilityDescription));
        OnPropertyChanged(nameof(IsOAuthProvider));
        OnPropertyChanged(nameof(IsImapFamily));
        OnPropertyChanged(nameof(IsPop3));
        OnPropertyChanged(nameof(IsDavContactChoiceAvailable));
        OnPropertyChanged(nameof(IsFixedLocalTaskSource));
        OnPropertyChanged(nameof(DavContactAvailabilityMessage));
        OnPropertyChanged(nameof(CalendarSourceOptions));
        OnPropertyChanged(nameof(ContactSourceOptions));
        OnPropertyChanged(nameof(TaskSourceOptions));

        ApplyProviderCapabilityDefaults();
        NotifyChoiceLabelsChanged();

        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnAccountNameChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedProviderSummaryDetail));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    private void OnMailModeChanged()
    {
        OnPropertyChanged(nameof(MailModeIndex));
        OnPropertyChanged(nameof(IsMailAccessEnabled));
        OnPropertyChanged(nameof(IsMailSynchronizationRangeVisible));
        OnPropertyChanged(nameof(IsInitialSynchronizationWarningVisible));
        OnPropertyChanged(nameof(IsMailChoiceProvider));
        OnPropertyChanged(nameof(IsMailChoiceOff));
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        NotifyCapabilitySelectionChanged();
    }

    private void OnCalendarModeChanged()
    {
        OnPropertyChanged(nameof(CalendarModeIndex));
        OnPropertyChanged(nameof(IsCalendarAccessEnabled));
        OnPropertyChanged(nameof(IsCalendarChoiceProvider));
        OnPropertyChanged(nameof(IsCalendarChoiceLocal));
        OnPropertyChanged(nameof(IsCalendarChoiceOff));
        OnPropertyChanged(nameof(IsCalendarOnlyServerHintVisible));
        NotifyCapabilitySelectionChanged();
    }

    private void OnContactModeChanged()
    {
        OnPropertyChanged(nameof(ContactModeIndex));
        OnPropertyChanged(nameof(IsContactAccessEnabled));
        OnPropertyChanged(nameof(IsContactChoiceProvider));
        OnPropertyChanged(nameof(IsContactChoiceLocal));
        OnPropertyChanged(nameof(IsContactChoiceOff));
        OnPropertyChanged(nameof(IsCardDavDiscoveryHintVisible));
        NotifyCapabilitySelectionChanged();
    }

    private void OnTaskModeChanged()
    {
        OnPropertyChanged(nameof(TaskModeIndex));
        OnPropertyChanged(nameof(IsTaskAccessEnabled));
        OnPropertyChanged(nameof(IsTaskChoiceProvider));
        OnPropertyChanged(nameof(IsTaskChoiceLocal));
        OnPropertyChanged(nameof(IsTaskChoiceOff));
        NotifyCapabilitySelectionChanged();
    }

    private void NotifyCapabilitySelectionChanged()
    {
        OnPropertyChanged(nameof(IsCapabilitySelectionMissing));
        OnPropertyChanged(nameof(ContinueButtonText));
        OnPropertyChanged(nameof(IsLocalOnlyHintVisible));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearColor() => SelectedColor = null;

    /// <summary>
    /// Answers one capability screen. The token is "capability:mode", for example "Calendar:Local".
    /// Navigation remains explicit through the Continue command.
    /// </summary>
    [RelayCommand]
    private void ChooseCapability(string token)
    {
        if (string.IsNullOrEmpty(token))
            return;

        var separatorIndex = token.IndexOf(':');

        if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
            return;

        if (!Enum.TryParse<AccountCapabilityMode>(token[(separatorIndex + 1)..], ignoreCase: true, out var mode))
            return;

        switch (token[..separatorIndex].ToLowerInvariant())
        {
            case "mail":
                MailMode = mode;
                break;
            case "calendar":
                CalendarMode = mode;
                break;
            case "contacts":
                ContactMode = mode;
                break;
            case "tasks":
                TaskMode = mode;
                break;
            default:
                return;
        }
    }

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

        if (WizardContext.IsGenericCustomMail && !Capabilities.RequiresRemoteService)
        {
            // Everything stays on this PC, so there is no server to sign in to.
            WizardContext.ImapCalDavSetupResult = CreateLocalOnlySetupResult();

            Messenger.Send(new BreadcrumbNavigationRequested(
                Translator.WelcomeWizard_Step3Title,
                WinoPage.AccountSetupProgressPage));
        }
        else if (WizardContext.IsGenericCustomMail)
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
        else if (SelectedProvider.SpecialImapProvider != SpecialImapProvider.None)
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

    /// <summary>
    /// A local-only account still carries server information so the IMAP-family account model
    /// stays whole, but no endpoint is set and nothing is validated.
    /// </summary>
    private ImapCalDavSetupResult CreateLocalOnlySetupResult() => new()
    {
        DisplayName = WizardContext.AccountName,
        EmailAddress = string.Empty,
        IsMailAccessGranted = false,
        IsCalendarAccessGranted = WizardContext.CalendarSupportMode != ImapCalendarSupportMode.Disabled,
        ShouldAppendMessagesToSentFolder = false,
        ServerInformation = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            IncomingServerType = CustomIncomingServerType.IMAP4,
            CalendarSupportMode = WizardContext.CalendarSupportMode,
            MaxConcurrentClients = 5,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected
        }
    };

    private static AccountCapabilityMode ToCapabilityMode(bool isEnabled, AccountIntegrationSource source)
    {
        if (!isEnabled) return AccountCapabilityMode.Off;

        return source == AccountIntegrationSource.Local
            ? AccountCapabilityMode.Local
            : AccountCapabilityMode.Provider;
    }

    private static int ToCapabilityModeIndex(AccountCapabilityMode mode)
        => mode switch
        {
            AccountCapabilityMode.Provider => 0,
            AccountCapabilityMode.Local => 1,
            _ => 2
        };

    private static AccountCapabilityMode FromCapabilityModeIndex(int index)
        => index switch
        {
            0 => AccountCapabilityMode.Provider,
            1 => AccountCapabilityMode.Local,
            _ => AccountCapabilityMode.Off
        };

    private AccountIntegrationSource ToIntegrationSource(AccountCapabilityMode mode)
    {
        // A capability that is off has no provider source; reporting one made later pages
        // treat a disabled CardDAV as enabled.
        if (mode != AccountCapabilityMode.Provider) return AccountIntegrationSource.Local;

        return IsOAuthProvider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Dav;
    }

    private string GetSelectedProviderCapabilityDescription()
    {
        if (SelectedProvider == null)
            return string.Empty;

        if (SelectedProvider.Type is MailProviderType.Outlook or MailProviderType.Gmail)
            return Translator.ProviderSelection_CapabilityProviderDescription_OAuth;

        if (SelectedProvider.SpecialImapProvider != SpecialImapProvider.None)
            return Translator.ProviderSelection_CapabilityProviderDescription_SpecialImap;

        return Translator.ProviderSelection_CapabilityProviderDescription_CustomServer;
    }

    private void ApplyProviderCapabilityDefaults()
    {
        MailMode = AccountCapabilityMode.Provider;
        CalendarMode = AccountCapabilityMode.Provider;
        ContactMode = AccountCapabilityMode.Provider;
        TaskMode = IsTaskProviderModeAvailable
            ? AccountCapabilityMode.Provider
            : AccountCapabilityMode.Local;

        CoerceUnavailableModes();
    }

    /// <summary>
    /// Falls back to the local mode when the selected provider cannot serve a capability,
    /// so a disabled segment is never the active one.
    /// </summary>
    private void CoerceUnavailableModes()
    {
        Capabilities.ConfigureProvider(
            IsCalendarProviderModeAvailable,
            IsContactProviderModeAvailable,
            IsTaskProviderModeAvailable,
            CalendarProviderModeLabel,
            ContactProviderModeLabel,
            TaskProviderModeLabel);

        OnPropertyChanged(nameof(IsCalendarProviderModeAvailable));
        OnPropertyChanged(nameof(IsContactProviderModeAvailable));
        OnPropertyChanged(nameof(IsTaskProviderModeAvailable));
        OnPropertyChanged(nameof(IsCalendarProviderRecommended));
        OnPropertyChanged(nameof(IsCalendarLocalRecommended));
        OnPropertyChanged(nameof(IsContactProviderRecommended));
        OnPropertyChanged(nameof(IsContactLocalRecommended));
        OnPropertyChanged(nameof(IsTaskProviderRecommended));
        OnPropertyChanged(nameof(IsTaskLocalRecommended));
        OnPropertyChanged(nameof(IsTaskProviderUnavailableHintVisible));
        OnPropertyChanged(nameof(IsCardDavDiscoveryHintVisible));
        OnPropertyChanged(nameof(MailProviderModeLabel));
        OnPropertyChanged(nameof(CalendarProviderModeLabel));
        OnPropertyChanged(nameof(ContactProviderModeLabel));
        OnPropertyChanged(nameof(TaskProviderModeLabel));
        NotifyChoiceLabelsChanged();
        NotifyCapabilitySelectionChanged();
    }

    private void NotifyChoiceLabelsChanged()
    {
        OnPropertyChanged(nameof(MailProviderChoiceDescription));
        OnPropertyChanged(nameof(CalendarProviderChoiceDescription));
        OnPropertyChanged(nameof(ContactProviderChoiceDescription));
        OnPropertyChanged(nameof(TaskProviderChoiceDescription));
    }
}
