
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Validation;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels;

public partial class ImapCalDavSettingsPageViewModel : MailBaseViewModel
{
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly ICalDavClient _calDavClient;
    private readonly IAccountService _accountService;
    private readonly IMailDialogService _mailDialogService;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly WelcomeWizardContext _wizardContext;

    private ImapCalDavSettingsPageMode _pageMode;
    private Guid _editingAccountId;
    private SpecialImapProvider _editingSpecialImapProvider;
    private TaskCompletionSource<ImapCalDavSetupResult> _completionSource;
    private AccountCreationDialogResult _accountCreationContext;
    private bool _isCompletionFinalized;
    private bool _localOnlyInfoShown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateMode))]
    [NotifyPropertyChangedFor(nameof(IsEditMode))]
    private string pageTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProviderHint))]
    private string providerHint = string.Empty;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string emailAddress = string.Empty;

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMailSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsMailPasswordInputVisible))]
    [NotifyPropertyChangedFor(nameof(IsMailActionsVisible))]
    private bool isMailSupportEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalendarModeSelectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsCalDavSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalCalendarModeSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedCalendarSupportDescription))]
    private bool isCalendarSupportEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalDavSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalCalendarModeSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedCalendarSupportDescription))]
    [NotifyPropertyChangedFor(nameof(SelectedCalendarSupportModeIndex))]
    private ImapCalendarSupportMode selectedCalendarSupportMode = ImapCalendarSupportMode.CalDav;

    [ObservableProperty]
    private string incomingServer = string.Empty;

    [ObservableProperty]
    private string incomingServerPort = string.Empty;

    [ObservableProperty]
    private string incomingServerUsername = string.Empty;

    [ObservableProperty]
    private string incomingServerPassword = string.Empty;

    [ObservableProperty]
    private string outgoingServer = string.Empty;

    [ObservableProperty]
    private string outgoingServerPort = string.Empty;

    [ObservableProperty]
    private string outgoingServerUsername = string.Empty;

    [ObservableProperty]
    private string outgoingServerPassword = string.Empty;

    [ObservableProperty]
    private string proxyServer = string.Empty;

    [ObservableProperty]
    private string proxyServerPort = string.Empty;

    [ObservableProperty]
    private string calDavServiceUrl = string.Empty;

    [ObservableProperty]
    private string calDavUsername = string.Empty;

    [ObservableProperty]
    private string calDavPassword = string.Empty;

    [ObservableProperty]
    private int maxConcurrentClients = 5;

    [ObservableProperty]
    private bool isImapValidationSucceeded;

    [ObservableProperty]
    private bool isCalDavValidationSucceeded;

    [ObservableProperty]
    private int selectedIncomingServerConnectionSecurityIndex;

    [ObservableProperty]
    private int selectedIncomingServerAuthenticationMethodIndex;

    [ObservableProperty]
    private int selectedOutgoingServerConnectionSecurityIndex;

    [ObservableProperty]
    private int selectedOutgoingServerAuthenticationMethodIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBasicSetupSelected))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedSetupSelected))]
    private int selectedSetupTabIndex;

    public bool IsCreateMode => _pageMode is ImapCalDavSettingsPageMode.Create or ImapCalDavSettingsPageMode.AddAccount;
    public bool IsEditMode => !IsCreateMode;
    public bool HasProviderHint => !string.IsNullOrWhiteSpace(ProviderHint);
    public bool IsBasicSetupSelected => SelectedSetupTabIndex == 0;
    public bool IsAdvancedSetupSelected => SelectedSetupTabIndex == 1;
    public bool IsMailSettingsVisible => IsMailSupportEnabled;
    public bool IsMailPasswordInputVisible => IsMailSupportEnabled;
    public bool IsMailActionsVisible => IsMailSupportEnabled;
    public bool IsCalendarModeSelectionVisible => IsCalendarSupportEnabled;
    public bool IsCalDavSettingsVisible => IsCalendarSupportEnabled && SelectedCalendarSupportMode == ImapCalendarSupportMode.CalDav;
    public bool IsLocalCalendarModeSelected => IsCalendarSupportEnabled && SelectedCalendarSupportMode == ImapCalendarSupportMode.LocalOnly;
    public string SubtitleText => Translator.ImapCalDavSettingsPage_Subtitle;
    public string BasicSectionTitleText => Translator.ImapCalDavSettingsPage_BasicSectionTitle;
    public string BasicSectionDescriptionText => Translator.ImapCalDavSettingsPage_BasicSectionDescription;
    public string DisplayNameHeaderText => Translator.IMAPSetupDialog_DisplayName;
    public string DisplayNamePlaceholderText => Translator.IMAPSetupDialog_DisplayNamePlaceholder;
    public string EmailAddressHeaderText => Translator.IMAPSetupDialog_MailAddress;
    public string EmailAddressPlaceholderText => Translator.IMAPSetupDialog_MailAddressPlaceholder;
    public string PasswordHeaderText => Translator.IMAPSetupDialog_Password;
    public string EnableMailSupportText => Translator.ProviderSelection_UseForMail;
    public string EnableCalendarSupportText => Translator.ImapCalDavSettingsPage_EnableCalendarSupport;
    public string AutoDiscoverButtonText => Translator.ImapCalDavSettingsPage_AutoDiscoverButton;
    public string BasicTabText => Translator.ImapCalDavSettingsPage_BasicTab;
    public string AdvancedTabText => Translator.ImapCalDavSettingsPage_AdvancedTab;
    public string AdvancedSectionTitleText => Translator.ImapCalDavSettingsPage_AdvancedSectionTitle;
    public string AdvancedSectionDescriptionText => Translator.ImapCalDavSettingsPage_AdvancedSectionDescription;
    public string IncomingSectionTitleText => Translator.IMAPSetupDialog_IMAPSettings;
    public string IncomingServerHeaderText => Translator.IMAPSetupDialog_IncomingMailServer;
    public string PortHeaderText => Translator.IMAPSetupDialog_IncomingMailServerPort;
    public string IncomingUsernameHeaderText => Translator.IMAPSetupDialog_Username;
    public string IncomingPasswordHeaderText => Translator.IMAPSetupDialog_Password;
    public string OutgoingSectionTitleText => Translator.IMAPSetupDialog_SMTPSettings;
    public string OutgoingServerHeaderText => Translator.IMAPSetupDialog_OutgoingMailServer;
    public string OutgoingUsernameHeaderText => Translator.IMAPSetupDialog_OutgoingMailServerUsername;
    public string OutgoingPasswordHeaderText => Translator.IMAPSetupDialog_OutgoingMailServerPassword;
    public string ConnectionSecurityHeaderText => Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader;
    public string AuthenticationMethodHeaderText => Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader;
    public string CalendarSectionTitleText => Translator.ImapCalDavSettingsPage_CalendarSectionTitle;
    public string CalendarSectionDescriptionText => Translator.ImapCalDavSettingsPage_CalendarSectionDescription;
    public string CalendarModeHeaderText => Translator.ImapCalDavSettingsPage_CalendarModeHeader;
    public string LocalCalendarLearnMoreText => Translator.ImapCalDavSettingsPage_LocalCalendarLearnMore;
    public string CalDavServiceUrlHeaderText => Translator.ImapCalDavSettingsPage_CalDavServiceUrl;
    public string CalDavUsernameHeaderText => Translator.ImapCalDavSettingsPage_CalDavUsername;
    public string CalDavPasswordHeaderText => Translator.ImapCalDavSettingsPage_CalDavPassword;
    public string TestImapButtonText => Translator.ImapCalDavSettingsPage_TestImapButton;
    public string TestCalDavButtonText => Translator.ImapCalDavSettingsPage_TestCalDavButton;
    public string SaveButtonText => Translator.Buttons_Save;
    public string CancelButtonText => Translator.Buttons_Cancel;

    public string SelectedCalendarSupportDescription => SelectedCalendarSupportMode switch
    {
        ImapCalendarSupportMode.CalDav => Translator.ImapCalDavSettingsPage_CalendarModeCalDavDescription,
        ImapCalendarSupportMode.LocalOnly => Translator.ImapCalDavSettingsPage_CalendarModeLocalOnlyDescription,
        _ => Translator.ImapCalDavSettingsPage_CalendarModeDisabledDescription
    };

    public List<ImapAuthenticationMethodModel> AvailableAuthenticationMethods { get; } =
    [
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.Auto, Translator.ImapAuthenticationMethod_Auto),
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.None, Translator.ImapAuthenticationMethod_None),
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.NormalPassword, Translator.ImapAuthenticationMethod_Plain),
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.EncryptedPassword, Translator.ImapAuthenticationMethod_EncryptedPassword),
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.Ntlm, Translator.ImapAuthenticationMethod_Ntlm),
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.CramMd5, Translator.ImapAuthenticationMethod_CramMD5),
        new ImapAuthenticationMethodModel(ImapAuthenticationMethod.DigestMd5, Translator.ImapAuthenticationMethod_DigestMD5)
    ];

    public List<ImapConnectionSecurityModel> AvailableConnectionSecurities { get; } =
    [
        new ImapConnectionSecurityModel(ImapConnectionSecurity.Auto, Translator.ImapConnectionSecurity_Auto),
        new ImapConnectionSecurityModel(ImapConnectionSecurity.SslTls, Translator.ImapConnectionSecurity_SslTls),
        new ImapConnectionSecurityModel(ImapConnectionSecurity.StartTls, Translator.ImapConnectionSecurity_StartTls),
        new ImapConnectionSecurityModel(ImapConnectionSecurity.None, Translator.ImapConnectionSecurity_None)
    ];

    public List<string> AvailableConnectionSecurityDisplayNames { get; } =
    [
        Translator.ImapConnectionSecurity_Auto,
        Translator.ImapConnectionSecurity_SslTls,
        Translator.ImapConnectionSecurity_StartTls,
        Translator.ImapConnectionSecurity_None
    ];

    public List<ImapCalendarSupportModeOption> AvailableCalendarSupportModes { get; } =
    [
        new ImapCalendarSupportModeOption(ImapCalendarSupportMode.CalDav, Translator.ImapCalDavSettingsPage_CalendarModeCalDav),
        new ImapCalendarSupportModeOption(ImapCalendarSupportMode.LocalOnly, Translator.ImapCalDavSettingsPage_CalendarModeLocalOnly),
        new ImapCalendarSupportModeOption(ImapCalendarSupportMode.Disabled, Translator.ImapCalDavSettingsPage_CalendarModeDisabled)
    ];

    public List<string> AvailableAuthenticationMethodDisplayNames { get; } =
    [
        Translator.ImapAuthenticationMethod_Auto,
        Translator.ImapAuthenticationMethod_None,
        Translator.ImapAuthenticationMethod_Plain,
        Translator.ImapAuthenticationMethod_EncryptedPassword,
        Translator.ImapAuthenticationMethod_Ntlm,
        Translator.ImapAuthenticationMethod_CramMD5,
        Translator.ImapAuthenticationMethod_DigestMD5
    ];

    public List<string> AvailableCalendarSupportModeTitles { get; } =
    [
        Translator.ImapCalDavSettingsPage_CalendarModeCalDav,
        Translator.ImapCalDavSettingsPage_CalendarModeLocalOnly,
        Translator.ImapCalDavSettingsPage_CalendarModeDisabled
    ];

    public int SelectedCalendarSupportModeIndex
    {
        get
        {
            var index = AvailableCalendarSupportModes.FindIndex(a => a.Mode == SelectedCalendarSupportMode);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= AvailableCalendarSupportModes.Count)
                return;

            var selectedMode = AvailableCalendarSupportModes[value].Mode;
            if (selectedMode != SelectedCalendarSupportMode)
            {
                SelectedCalendarSupportMode = selectedMode;
            }
        }
    }

    public ImapCalDavSettingsPageViewModel(IAutoDiscoveryService autoDiscoveryService,
                                           ICalDavClient calDavClient,
                                           IAccountService accountService,
                                           IMailDialogService mailDialogService,
                                           ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
                                           WelcomeWizardContext wizardContext)
    {
        _autoDiscoveryService = autoDiscoveryService;
        _calDavClient = calDavClient;
        _accountService = accountService;
        _mailDialogService = mailDialogService;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _wizardContext = wizardContext;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not ImapCalDavSettingsNavigationContext context)
            return;

        _pageMode = context.Mode;
        _editingAccountId = context.AccountId;
        _completionSource = context.CompletionSource;
        _accountCreationContext = context.AccountCreationDialogResult;
        _isCompletionFinalized = false;
        _localOnlyInfoShown = false;
        SelectedSetupTabIndex = 0;

        if (_pageMode is ImapCalDavSettingsPageMode.Create or ImapCalDavSettingsPageMode.Wizard or ImapCalDavSettingsPageMode.AddAccount)
        {
            PageTitle = Translator.ImapCalDavSettingsPage_TitleCreate;
            ApplyCreateContextDefaults(context.AccountCreationDialogResult);
        }
        else
        {
            PageTitle = Translator.ImapCalDavSettingsPage_TitleEdit;
            await InitializeEditModeAsync(context.AccountId);
        }
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        if (_pageMode == ImapCalDavSettingsPageMode.Create && !_isCompletionFinalized)
        {
            _completionSource?.TrySetResult(null);
            _isCompletionFinalized = true;
        }

        base.OnNavigatedFrom(mode, parameters);
    }

    public bool IsWizardMode => _pageMode == ImapCalDavSettingsPageMode.Wizard;

    [RelayCommand]
    private async Task AutoDiscoverSettingsAsync()
    {
        try
        {
            var minimalSettings = BuildMinimalSettingsOrThrow();
            await AutoDiscoverAndApplySettingsAsync(minimalSettings);

            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationSuccess_Title,
                Translator.ImapCalDavSettingsPage_AutoDiscoverySuccessMessage,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationFailed_Title,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task TestImapConnectionAsync()
    {
        try
        {
            ValidateCapabilitySelection();
            await EnsureImapSettingsPreparedAsync().ConfigureAwait(false);
            var serverInformation = BuildServerInformation();

            ValidateImapSettings(serverInformation);
            await ValidateImapConnectivityAsync(serverInformation).ConfigureAwait(false);

            IsImapValidationSucceeded = true;

            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationSuccess_Title,
                Translator.ImapCalDavSettingsPage_ImapTestSuccessMessage,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            IsImapValidationSucceeded = false;

            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationFailed_Title,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task TestCalDavConnectionAsync()
    {
        try
        {
            if (!IsCalendarSupportEnabled || SelectedCalendarSupportMode != ImapCalendarSupportMode.CalDav)
                throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavNotRequiredMessage);

            TryApplyKnownProviderSettingsIfNeeded(requireCompleteImapSettings: false, requireCompleteCalDavSettings: true);
            var serverInformation = BuildServerInformation();
            ValidateCalDavSettings(serverInformation);
            await ValidateCalDavConnectivityAsync(serverInformation).ConfigureAwait(false);

            IsCalDavValidationSucceeded = true;

            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationSuccess_Title,
                Translator.ImapCalDavSettingsPage_CalDavTestSuccessMessage,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            IsCalDavValidationSucceeded = false;

            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationFailed_Title,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }
    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            ValidateCapabilitySelection();
            await EnsureImapSettingsPreparedAsync();

            var serverInformation = BuildServerInformation();

            ValidateIdentitySettings();
            ValidateImapSettings(serverInformation);
            ValidateCalendarModeSpecificSettings(serverInformation);

            var excludedAccountId = _pageMode == ImapCalDavSettingsPageMode.Edit
                ? _editingAccountId
                : (Guid?)null;

            if (!await ValidateAccountUniquenessAsync(excludedAccountId))
                return;

            if (IsMailSupportEnabled)
            {
                await ValidateImapConnectivityAsync(serverInformation);
                IsImapValidationSucceeded = true;
            }
            else
            {
                IsImapValidationSucceeded = false;
            }

            if (serverInformation.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
            {
                await ValidateCalDavConnectivityAsync(serverInformation);
                IsCalDavValidationSucceeded = true;
            }
            else
            {
                IsCalDavValidationSucceeded = false;
            }

            if (_pageMode == ImapCalDavSettingsPageMode.Wizard)
            {
                CompleteWizardFlow(serverInformation);
                return;
            }

            if (_pageMode == ImapCalDavSettingsPageMode.AddAccount)
            {
                CompleteAddAccountFlow(serverInformation);
                return;
            }

            if (_pageMode == ImapCalDavSettingsPageMode.Create)
            {
                CompleteCreateFlow(serverInformation);
                return;
            }

            await SaveEditFlowAsync(serverInformation);
        }
        catch (Exception ex)
        {
            _mailDialogService.InfoBarMessage(
                Translator.IMAPSetupDialog_ValidationFailed_Title,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_pageMode == ImapCalDavSettingsPageMode.Create && !_isCompletionFinalized)
        {
            _completionSource?.TrySetResult(null);
            _isCompletionFinalized = true;
        }

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    private void CompleteWizardFlow(CustomServerInformation serverInformation)
        => ContinueAccountCreationFlow(serverInformation);

    private void CompleteAddAccountFlow(CustomServerInformation serverInformation)
        => ContinueAccountCreationFlow(serverInformation);

    private void ContinueAccountCreationFlow(CustomServerInformation serverInformation)
    {
        serverInformation.Id = Guid.NewGuid();
        serverInformation.AccountId = Guid.Empty;

        _wizardContext.ImapCalDavSetupResult = new ImapCalDavSetupResult
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = EmailAddress.Trim(),
            IsMailAccessGranted = IsMailSupportEnabled,
            IsCalendarAccessGranted = serverInformation.CalendarSupportMode != ImapCalendarSupportMode.Disabled,
            ServerInformation = serverInformation
        };

        Messenger.Send(new BreadcrumbNavigationRequested(Translator.WelcomeWizard_Step3Title, WinoPage.AccountSetupProgressPage));
    }

    [RelayCommand]
    private Task ShowLocalCalendarExplanationAsync()
        => _mailDialogService.ShowMessageAsync(
            Translator.ImapCalDavSettingsPage_LocalCalendarDialogMessage,
            Translator.ImapCalDavSettingsPage_LocalCalendarDialogTitle,
            WinoCustomMessageDialogIcon.Information);

    partial void OnIsCalendarSupportEnabledChanged(bool value)
    {
        if (!value && SelectedCalendarSupportMode != ImapCalendarSupportMode.Disabled)
        {
            SelectedCalendarSupportMode = ImapCalendarSupportMode.Disabled;
        }
        else if (value && SelectedCalendarSupportMode == ImapCalendarSupportMode.Disabled)
        {
            SelectedCalendarSupportMode = ImapCalendarSupportMode.CalDav;
        }
    }

    partial void OnIsMailSupportEnabledChanged(bool value)
    {
        if (!value)
        {
            IsImapValidationSucceeded = false;
        }
    }

    partial void OnSelectedCalendarSupportModeChanged(ImapCalendarSupportMode value)
    {
        if (value == ImapCalendarSupportMode.LocalOnly && !_localOnlyInfoShown)
        {
            _localOnlyInfoShown = true;
            _ = ShowLocalCalendarExplanationAsync();
        }

        if (value != ImapCalendarSupportMode.CalDav)
        {
            IsCalDavValidationSucceeded = false;
        }
    }

    partial void OnEmailAddressChanged(string oldValue, string newValue)
    {
        var previousAddress = oldValue?.Trim() ?? string.Empty;
        var currentAddress = newValue?.Trim() ?? string.Empty;

        ApplyCredentialDefaultsForAddress(previousAddress, currentAddress);
        ApplyManualServerDefaultsForAddress(previousAddress, currentAddress);

        IsImapValidationSucceeded = false;
        IsCalDavValidationSucceeded = false;
    }

    partial void OnPasswordChanged(string oldValue, string newValue)
    {
        var previousPassword = oldValue ?? string.Empty;
        var currentPassword = newValue ?? string.Empty;

        IncomingServerPassword = ReplaceIfEmptyOrMatchingPrevious(IncomingServerPassword, previousPassword, currentPassword);
        OutgoingServerPassword = ReplaceIfEmptyOrMatchingPrevious(OutgoingServerPassword, previousPassword, currentPassword);
        CalDavPassword = ReplaceIfEmptyOrMatchingPrevious(CalDavPassword, previousPassword, currentPassword);

        IsImapValidationSucceeded = false;
        IsCalDavValidationSucceeded = false;
    }

    private async Task InitializeEditModeAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
            throw new InvalidOperationException(Translator.Exception_NullAssignedAccount);

        _editingSpecialImapProvider = account.SpecialImapProvider;
        DisplayName = account.SenderName ?? string.Empty;
        EmailAddress = account.Address ?? string.Empty;
        ApplyProviderHint(_editingSpecialImapProvider);

        ApplyServerInformation(account.ServerInformation);
        IsMailSupportEnabled = account.IsMailAccessGranted;

        if (account.ServerInformation != null)
        {
            SelectedCalendarSupportMode = account.ServerInformation.CalendarSupportMode;
        }

        if (SelectedCalendarSupportMode == ImapCalendarSupportMode.Disabled && account.IsCalendarAccessGranted)
        {
            SelectedCalendarSupportMode = ImapCalendarSupportMode.CalDav;
        }

        IsCalendarSupportEnabled = SelectedCalendarSupportMode != ImapCalendarSupportMode.Disabled;
    }

    private void ApplyCreateContextDefaults(AccountCreationDialogResult accountCreationDialogResult)
    {
        DisplayName = accountCreationDialogResult?.AccountName ?? string.Empty;
        EmailAddress = accountCreationDialogResult?.SpecialImapProviderDetails?.Address ?? string.Empty;
        Password = accountCreationDialogResult?.SpecialImapProviderDetails?.Password ?? string.Empty;
        var normalizedEmail = !string.IsNullOrWhiteSpace(EmailAddress) && !EmailAddress.Contains('@')
            ? $"{EmailAddress}@icloud.com"
            : EmailAddress;
        var iCloudMailboxUsername = GetICloudMailboxUsername(normalizedEmail);

        if (!string.IsNullOrWhiteSpace(accountCreationDialogResult?.SpecialImapProviderDetails?.SenderName))
            DisplayName = accountCreationDialogResult.SpecialImapProviderDetails.SenderName;

        IsMailSupportEnabled = accountCreationDialogResult?.IsMailAccessGranted != false;
        IsCalendarSupportEnabled = accountCreationDialogResult?.IsCalendarAccessGranted == true;
        SelectedCalendarSupportMode = accountCreationDialogResult?.SpecialImapProviderDetails?.CalendarSupportMode
            ?? (IsCalendarSupportEnabled ? ImapCalendarSupportMode.CalDav : ImapCalendarSupportMode.Disabled);

        var specialProvider = accountCreationDialogResult?.SpecialImapProviderDetails?.SpecialImapProvider ?? SpecialImapProvider.None;
        _editingSpecialImapProvider = specialProvider;
        ApplyProviderHint(specialProvider);

        switch (specialProvider)
        {
            case SpecialImapProvider.iCloud:
                ApplySpecialProviderDefaults(
                    "imap.mail.me.com",
                    "993",
                    iCloudMailboxUsername,
                    "smtp.mail.me.com",
                    "587",
                    iCloudMailboxUsername,
                    Password,
                    "https://caldav.icloud.com/",
                    normalizedEmail,
                    Password);
                break;
            case SpecialImapProvider.Yahoo:
                ApplySpecialProviderDefaults(
                    "imap.mail.yahoo.com",
                    "993",
                    EmailAddress,
                    "smtp.mail.yahoo.com",
                    "587",
                    EmailAddress,
                    Password,
                    "https://caldav.calendar.yahoo.com/",
                    EmailAddress,
                    Password);
                break;
        }
    }

    private void ApplySpecialProviderDefaults(string incomingServer,
                                              string incomingPort,
                                              string incomingUsername,
                                              string outgoingServer,
                                              string outgoingPort,
                                              string outgoingUsername,
                                              string password,
                                              string calDavServiceUrl,
                                              string calDavUsername,
                                              string calDavPassword)
    {
        IncomingServer = incomingServer;
        IncomingServerPort = incomingPort;
        IncomingServerUsername = incomingUsername;
        IncomingServerPassword = password;

        OutgoingServer = outgoingServer;
        OutgoingServerPort = outgoingPort;
        OutgoingServerUsername = outgoingUsername;
        OutgoingServerPassword = password;
        CalDavServiceUrl = calDavServiceUrl;
        CalDavUsername = calDavUsername;
        CalDavPassword = calDavPassword;

        SelectedIncomingServerConnectionSecurityIndex = 0;
        SelectedIncomingServerAuthenticationMethodIndex = 0;
        SelectedOutgoingServerConnectionSecurityIndex = 0;
        SelectedOutgoingServerAuthenticationMethodIndex = 0;
    }

    private void ApplyCredentialDefaultsForAddress(string previousAddress, string currentAddress)
    {
        IncomingServerUsername = ReplaceIfEmptyOrMatchingPrevious(IncomingServerUsername, previousAddress, currentAddress);
        OutgoingServerUsername = ReplaceIfEmptyOrMatchingPrevious(OutgoingServerUsername, previousAddress, currentAddress);
        CalDavUsername = ReplaceIfEmptyOrMatchingPrevious(CalDavUsername, previousAddress, currentAddress);
    }

    private void ApplyManualServerDefaultsForAddress(string previousAddress, string currentAddress)
    {
        if (!MailAccountAddressValidator.TryGetDomain(currentAddress, out var currentDomain) ||
            !MailAccountAddressValidator.IsImplicitlyResolvableHost(currentDomain))
        {
            return;
        }

        MailAccountAddressValidator.TryGetDomain(previousAddress, out var previousDomain);

        IncomingServer = ReplaceIfEmptyOrMatchingPrevious(IncomingServer, previousDomain, currentDomain);
        OutgoingServer = ReplaceIfEmptyOrMatchingPrevious(OutgoingServer, previousDomain, currentDomain);

        if (string.IsNullOrWhiteSpace(IncomingServerPort))
            IncomingServerPort = "993";

        if (string.IsNullOrWhiteSpace(OutgoingServerPort))
            OutgoingServerPort = "587";
    }

    private static string GetICloudMailboxUsername(string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            return string.Empty;

        var normalizedAddress = emailAddress.Trim();
        var atIndex = normalizedAddress.IndexOf('@');

        return atIndex > 0
            ? normalizedAddress[..atIndex]
            : normalizedAddress;
    }

    private static string ReplaceIfEmptyOrMatchingPrevious(string currentValue, string previousValue, string replacementValue)
    {
        var normalizedCurrentValue = currentValue?.Trim() ?? string.Empty;
        var normalizedPreviousValue = previousValue?.Trim() ?? string.Empty;
        var normalizedReplacementValue = replacementValue?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedReplacementValue))
            return currentValue ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedCurrentValue))
            return normalizedReplacementValue;

        return string.Equals(normalizedCurrentValue, normalizedPreviousValue, StringComparison.OrdinalIgnoreCase)
            ? normalizedReplacementValue
            : currentValue ?? string.Empty;
    }

    private void ApplyServerInformation(CustomServerInformation serverInformation)
    {
        if (serverInformation == null)
            return;

        IncomingServer = serverInformation.IncomingServer ?? string.Empty;
        IncomingServerPort = serverInformation.IncomingServerPort ?? string.Empty;
        IncomingServerUsername = serverInformation.IncomingServerUsername ?? string.Empty;
        IncomingServerPassword = serverInformation.IncomingServerPassword ?? string.Empty;

        OutgoingServer = serverInformation.OutgoingServer ?? string.Empty;
        OutgoingServerPort = serverInformation.OutgoingServerPort ?? string.Empty;
        OutgoingServerUsername = serverInformation.OutgoingServerUsername ?? string.Empty;
        OutgoingServerPassword = serverInformation.OutgoingServerPassword ?? string.Empty;

        ProxyServer = serverInformation.ProxyServer ?? string.Empty;
        ProxyServerPort = serverInformation.ProxyServerPort ?? string.Empty;
        MaxConcurrentClients = serverInformation.MaxConcurrentClients <= 0 ? 5 : serverInformation.MaxConcurrentClients;

        CalDavServiceUrl = serverInformation.CalDavServiceUrl ?? string.Empty;
        CalDavUsername = serverInformation.CalDavUsername ?? string.Empty;
        CalDavPassword = serverInformation.CalDavPassword ?? string.Empty;

        if (string.IsNullOrWhiteSpace(CalDavUsername))
            CalDavUsername = EmailAddress;

        if (string.IsNullOrWhiteSpace(CalDavPassword))
            CalDavPassword = IncomingServerPassword;

        SelectedIncomingServerAuthenticationMethodIndex = FindAuthenticationMethodIndex(serverInformation.IncomingAuthenticationMethod);
        SelectedIncomingServerConnectionSecurityIndex = FindConnectionSecurityIndex(serverInformation.IncomingServerSocketOption);
        SelectedOutgoingServerAuthenticationMethodIndex = FindAuthenticationMethodIndex(serverInformation.OutgoingAuthenticationMethod);
        SelectedOutgoingServerConnectionSecurityIndex = FindConnectionSecurityIndex(serverInformation.OutgoingServerSocketOption);
    }

    private async Task EnsureImapSettingsPreparedAsync()
    {
        if (!IsMailSupportEnabled)
            return;

        if (HasCompleteImapSettings())
            return;

        if (TryApplyKnownProviderSettingsIfNeeded(requireCompleteImapSettings: true, requireCompleteCalDavSettings: false))
            return;

        var minimalSettings = BuildMinimalSettingsOrThrow();
        await AutoDiscoverAndApplySettingsAsync(minimalSettings);

        if (!HasCompleteImapSettings())
            throw new InvalidOperationException(Translator.Exception_ImapAutoDiscoveryFailed);
    }

    private async Task AutoDiscoverAndApplySettingsAsync(AutoDiscoveryMinimalSettings minimalSettings)
    {
        if (TryApplyKnownProviderSettings(alwaysApplyForKnownProvider: true))
            return;

        var discoverySettings = await _autoDiscoveryService.GetAutoDiscoverySettings(minimalSettings).ConfigureAwait(false);

        if (discoverySettings == null)
            throw new InvalidOperationException(Translator.Exception_ImapAutoDiscoveryFailed);

        discoverySettings.UserMinimalSettings = minimalSettings;

        var serverInformation = discoverySettings.ToServerInformation();
        if (serverInformation == null)
            throw new InvalidOperationException(Translator.Exception_ImapAutoDiscoveryFailed);

        await ExecuteUIThread(async () =>
        {
            ApplyServerInformation(serverInformation);

            if (IsCalendarSupportEnabled && SelectedCalendarSupportMode == ImapCalendarSupportMode.CalDav)
            {
                var discoveredCalDavUri = await _autoDiscoveryService.DiscoverCalDavServiceUriAsync(minimalSettings.Email);
                if (discoveredCalDavUri != null)
                {
                    CalDavServiceUrl = discoveredCalDavUri.ToString();
                }

                if (string.IsNullOrWhiteSpace(CalDavUsername))
                    CalDavUsername = minimalSettings.Email;

                if (string.IsNullOrWhiteSpace(CalDavPassword))
                    CalDavPassword = minimalSettings.Password;
            }
        });
    }
    private async Task ValidateImapConnectivityAsync(CustomServerInformation serverInformation)
    {
        var connectivityResult = await SynchronizationManager.Instance
            .TestImapConnectivityAsync(serverInformation, allowSSLHandshake: false)
            .ConfigureAwait(false);

        if (connectivityResult.IsCertificateUIRequired)
        {
            var certificateMessage =
                $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateIssuer}: {connectivityResult.CertificateIssuer}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidFrom}: {connectivityResult.CertificateValidFromDateString}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidTo}: {connectivityResult.CertificateExpirationDateString}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1}";

            var allowCertificate = await _mailDialogService
                .ShowConfirmationDialogAsync(certificateMessage, Translator.GeneralTitle_Warning, Translator.Buttons_Allow)
                .ConfigureAwait(false);

            if (!allowCertificate)
                throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateDenied);

            connectivityResult = await SynchronizationManager.Instance
                .TestImapConnectivityAsync(serverInformation, allowSSLHandshake: true)
                .ConfigureAwait(false);
        }

        if (!connectivityResult.IsSuccess)
            throw new InvalidOperationException(connectivityResult.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage);
    }

    private async Task ValidateCalDavConnectivityAsync(CustomServerInformation serverInformation)
    {
        ValidateCalDavSettings(serverInformation);

        var uri = new Uri(serverInformation.CalDavServiceUrl, UriKind.Absolute);
        var username = serverInformation.CalDavUsername;
        var password = serverInformation.CalDavPassword;

        var settings = new CalDavConnectionSettings
        {
            ServiceUri = uri,
            Username = username,
            Password = password
        };

        await _calDavClient.DiscoverCalendarsAsync(settings).ConfigureAwait(false);
    }

    private void CompleteCreateFlow(CustomServerInformation serverInformation)
    {
        if (_completionSource == null || _isCompletionFinalized)
            return;

        serverInformation.Id = Guid.NewGuid();
        serverInformation.AccountId = Guid.Empty;

        _completionSource.TrySetResult(new ImapCalDavSetupResult
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = EmailAddress.Trim(),
            IsMailAccessGranted = IsMailSupportEnabled,
            IsCalendarAccessGranted = serverInformation.CalendarSupportMode != ImapCalendarSupportMode.Disabled,
            ServerInformation = serverInformation
        });

        _isCompletionFinalized = true;

        _mailDialogService.InfoBarMessage(
            Translator.IMAPSetupDialog_ValidationSuccess_Title,
            Translator.ImapCalDavSettingsPage_SaveSuccessMessage,
            InfoBarMessageType.Success);

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    private async Task<bool> ValidateAccountUniquenessAsync(Guid? excludedAccountId)
    {
        var accountName = (_pageMode == ImapCalDavSettingsPageMode.Create
                           || _pageMode == ImapCalDavSettingsPageMode.Wizard
                           || _pageMode == ImapCalDavSettingsPageMode.AddAccount)
            ? _accountCreationContext?.AccountName
            : null;

        if (!string.IsNullOrWhiteSpace(accountName) &&
            await _accountService.AccountNameExistsAsync(accountName, excludedAccountId).ConfigureAwait(false))
        {
            _mailDialogService.InfoBarMessage(
                Translator.DialogMessage_AccountExistsTitle,
                Translator.DialogMessage_AccountNameExistsMessage,
                InfoBarMessageType.Error);
            return false;
        }

        if (await _accountService.AccountAddressExistsAsync(EmailAddress, excludedAccountId).ConfigureAwait(false))
        {
            _mailDialogService.InfoBarMessage(
                Translator.DialogMessage_AccountExistsTitle,
                Translator.DialogMessage_AccountAddressExistsMessage,
                InfoBarMessageType.Error);
            return false;
        }

        return true;
    }

    private static void ValidateCapabilitySelection(bool isMailEnabled, bool isCalendarEnabled)
    {
        if (!isMailEnabled && !isCalendarEnabled)
            throw new InvalidOperationException(Translator.ProviderSelection_CapabilityValidationMessage);
    }

    private void ValidateCapabilitySelection()
        => ValidateCapabilitySelection(IsMailSupportEnabled, IsCalendarSupportEnabled);

    private async Task SaveEditFlowAsync(CustomServerInformation serverInformation)
    {
        var account = await _accountService.GetAccountAsync(_editingAccountId);
        if (account == null)
            throw new InvalidOperationException(Translator.Exception_NullAssignedAccount);

        account.SenderName = DisplayName.Trim();
        account.Address = EmailAddress.Trim();
        account.IsMailAccessGranted = IsMailSupportEnabled;
        account.IsCalendarAccessGranted = serverInformation.CalendarSupportMode != ImapCalendarSupportMode.Disabled;

        serverInformation.Id = account.ServerInformation?.Id ?? Guid.NewGuid();
        serverInformation.AccountId = account.Id;

        account.ServerInformation = serverInformation;
        account.AttentionReason = AccountAttentionReason.None;

        await _accountService.UpdateAccountCustomServerInformationAsync(serverInformation);
        await _accountService.UpdateAccountAsync(account);

        if (account.IsMailAccessGranted)
        {
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
        }

        if (account.IsCalendarAccessGranted)
        {
            Messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            }));
        }

        _mailDialogService.InfoBarMessage(
            Translator.IMAPSetupDialog_ValidationSuccess_Title,
            Translator.ImapCalDavSettingsPage_SaveSuccessMessage,
            InfoBarMessageType.Success);

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    private AutoDiscoveryMinimalSettings BuildMinimalSettingsOrThrow()
    {
        ValidateIdentitySettings();

        if (string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationPasswordRequired);

        return new AutoDiscoveryMinimalSettings
        {
            DisplayName = DisplayName.Trim(),
            Email = EmailAddress.Trim(),
            Password = Password
        };
    }

    private CustomServerInformation BuildServerInformation()
    {
        var incomingAuth = GetAuthenticationMethodByIndex(SelectedIncomingServerAuthenticationMethodIndex);
        var incomingSecurity = GetConnectionSecurityByIndex(SelectedIncomingServerConnectionSecurityIndex);
        var outgoingAuth = GetAuthenticationMethodByIndex(SelectedOutgoingServerAuthenticationMethodIndex);
        var outgoingSecurity = GetConnectionSecurityByIndex(SelectedOutgoingServerConnectionSecurityIndex);

        var mode = IsCalendarSupportEnabled ? SelectedCalendarSupportMode : ImapCalendarSupportMode.Disabled;

        var calDavUser = (CalDavUsername ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(calDavUser))
            calDavUser = (EmailAddress ?? string.Empty).Trim();

        var calDavPassword = string.IsNullOrWhiteSpace(CalDavPassword)
            ? IncomingServerPassword
            : CalDavPassword;

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            Address = (EmailAddress ?? string.Empty).Trim(),
            IncomingServer = (IncomingServer ?? string.Empty).Trim(),
            IncomingServerPort = (IncomingServerPort ?? string.Empty).Trim(),
            IncomingServerUsername = (IncomingServerUsername ?? string.Empty).Trim(),
            IncomingServerPassword = IncomingServerPassword ?? string.Empty,
            IncomingServerType = CustomIncomingServerType.IMAP4,
            IncomingAuthenticationMethod = incomingAuth,
            IncomingServerSocketOption = incomingSecurity,
            OutgoingServer = (OutgoingServer ?? string.Empty).Trim(),
            OutgoingServerPort = (OutgoingServerPort ?? string.Empty).Trim(),
            OutgoingServerUsername = (OutgoingServerUsername ?? string.Empty).Trim(),
            OutgoingServerPassword = OutgoingServerPassword ?? string.Empty,
            OutgoingAuthenticationMethod = outgoingAuth,
            OutgoingServerSocketOption = outgoingSecurity,
            ProxyServer = (ProxyServer ?? string.Empty).Trim(),
            ProxyServerPort = (ProxyServerPort ?? string.Empty).Trim(),
            MaxConcurrentClients = MaxConcurrentClients <= 0 ? 5 : MaxConcurrentClients,
            CalendarSupportMode = mode,
            CalDavServiceUrl = mode == ImapCalendarSupportMode.CalDav ? (CalDavServiceUrl ?? string.Empty).Trim() : string.Empty,
            CalDavUsername = mode == ImapCalendarSupportMode.CalDav ? calDavUser : string.Empty,
            CalDavPassword = mode == ImapCalendarSupportMode.CalDav ? calDavPassword : string.Empty
        };
    }

    private void ValidateIdentitySettings()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationDisplayNameRequired);

        if (string.IsNullOrWhiteSpace(EmailAddress))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationEmailRequired);

        if (!MailAccountAddressValidator.IsValid(EmailAddress))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationEmailInvalid);
    }

    private static bool IsValidPort(string portText)
        => int.TryParse(portText, out var value) && value > 0 && value <= 65535;

    private void ValidateImapSettings(CustomServerInformation serverInformation)
    {
        if (!IsMailSupportEnabled)
            return;

        ValidateIdentitySettings();

        if (string.IsNullOrWhiteSpace(serverInformation.IncomingServer))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationIncomingServerRequired);

        if (string.IsNullOrWhiteSpace(serverInformation.IncomingServerPort))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationIncomingPortRequired);

        if (!IsValidPort(serverInformation.IncomingServerPort))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationIncomingPortInvalid);

        if (string.IsNullOrWhiteSpace(serverInformation.IncomingServerUsername))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationUsernameRequired);

        if (string.IsNullOrWhiteSpace(serverInformation.IncomingServerPassword))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationPasswordRequired);

        if (string.IsNullOrWhiteSpace(serverInformation.OutgoingServer))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationOutgoingServerRequired);

        if (string.IsNullOrWhiteSpace(serverInformation.OutgoingServerPort))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationOutgoingPortRequired);

        if (!IsValidPort(serverInformation.OutgoingServerPort))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationOutgoingPortInvalid);

        if (string.IsNullOrWhiteSpace(serverInformation.OutgoingServerUsername))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationOutgoingUsernameRequired);

        if (string.IsNullOrWhiteSpace(serverInformation.OutgoingServerPassword))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationOutgoingPasswordRequired);
    }

    private void ValidateCalendarModeSpecificSettings(CustomServerInformation serverInformation)
    {
        if (serverInformation.CalendarSupportMode != ImapCalendarSupportMode.CalDav)
            return;

        ValidateCalDavSettings(serverInformation);
    }

    private void ValidateCalDavSettings(CustomServerInformation serverInformation)
    {
        if (string.IsNullOrWhiteSpace(serverInformation.CalDavServiceUrl))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUrlRequired);

        if (!Uri.TryCreate(serverInformation.CalDavServiceUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUrlInvalid);

        if (string.IsNullOrWhiteSpace(serverInformation.CalDavUsername))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUsernameRequired);

        if (string.IsNullOrWhiteSpace(serverInformation.CalDavPassword))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavPasswordRequired);
    }

    private void ApplyProviderHint(SpecialImapProvider provider)
    {
        ProviderHint = provider switch
        {
            SpecialImapProvider.iCloud => Translator.ImapCalDavSettingsPage_ICloudHint,
            SpecialImapProvider.Yahoo => Translator.ImapCalDavSettingsPage_YahooHint,
            _ => string.Empty
        };
    }

    private bool TryApplyKnownProviderSettingsIfNeeded(bool requireCompleteImapSettings, bool requireCompleteCalDavSettings)
    {
        var needsImapSettings = IsMailSupportEnabled && requireCompleteImapSettings && !HasCompleteImapSettings();
        var needsCalDavSettings = requireCompleteCalDavSettings
                                  && IsCalendarSupportEnabled
                                  && SelectedCalendarSupportMode == ImapCalendarSupportMode.CalDav
                                  && !HasCompleteCalDavSettings();

        if (!needsImapSettings && !needsCalDavSettings)
            return false;

        return TryApplyKnownProviderSettings(alwaysApplyForKnownProvider: false);
    }

    private bool TryApplyKnownProviderSettings(bool alwaysApplyForKnownProvider)
    {
        if (_editingSpecialImapProvider is not (SpecialImapProvider.iCloud or SpecialImapProvider.Yahoo))
            return false;

        var effectivePassword = GetKnownProviderPasswordCandidate();
        if (string.IsNullOrWhiteSpace(EmailAddress) || string.IsNullOrWhiteSpace(effectivePassword))
            return false;

        if (!alwaysApplyForKnownProvider && HasCompleteImapSettings() && HasCompleteCalDavSettings())
            return false;

        var mode = IsCalendarSupportEnabled ? SelectedCalendarSupportMode : ImapCalendarSupportMode.Disabled;
        var providerDetails = new SpecialImapProviderDetails(
            EmailAddress.Trim(),
            effectivePassword,
            DisplayName.Trim(),
            _editingSpecialImapProvider,
            mode);

        var serverInformation = _specialImapProviderConfigResolver.GetServerInformation(
            new MailAccount
            {
                Address = EmailAddress.Trim(),
                SenderName = DisplayName.Trim(),
                ProviderType = MailProviderType.IMAP4,
                SpecialImapProvider = _editingSpecialImapProvider,
                IsMailAccessGranted = IsMailSupportEnabled,
                IsCalendarAccessGranted = mode != ImapCalendarSupportMode.Disabled
            },
            new AccountCreationDialogResult(
                MailProviderType.IMAP4,
                DisplayName.Trim(),
                providerDetails,
                string.Empty,
                _wizardContext.SelectedInitialSynchronizationRange,
                IsMailSupportEnabled,
                mode != ImapCalendarSupportMode.Disabled));

        if (serverInformation == null)
            return false;

        serverInformation.ProxyServer = (ProxyServer ?? string.Empty).Trim();
        serverInformation.ProxyServerPort = (ProxyServerPort ?? string.Empty).Trim();
        serverInformation.MaxConcurrentClients = MaxConcurrentClients <= 0 ? serverInformation.MaxConcurrentClients : MaxConcurrentClients;

        ApplyServerInformation(serverInformation);
        Password = effectivePassword;
        return true;
    }

    private string GetKnownProviderPasswordCandidate()
    {
        if (!string.IsNullOrWhiteSpace(Password))
            return Password;

        if (!string.IsNullOrWhiteSpace(IncomingServerPassword))
            return IncomingServerPassword;

        if (!string.IsNullOrWhiteSpace(OutgoingServerPassword))
            return OutgoingServerPassword;

        return CalDavPassword ?? string.Empty;
    }

    private bool HasCompleteCalDavSettings()
        => !IsCalendarSupportEnabled
           || SelectedCalendarSupportMode != ImapCalendarSupportMode.CalDav
           || (!string.IsNullOrWhiteSpace(CalDavServiceUrl)
               && Uri.TryCreate(CalDavServiceUrl, UriKind.Absolute, out _)
               && !string.IsNullOrWhiteSpace(CalDavUsername)
               && !string.IsNullOrWhiteSpace(CalDavPassword));

    private bool HasCompleteImapSettings()
        => !IsMailSupportEnabled
           || (!string.IsNullOrWhiteSpace(IncomingServer)
           && !string.IsNullOrWhiteSpace(IncomingServerPort)
           && !string.IsNullOrWhiteSpace(IncomingServerUsername)
           && !string.IsNullOrWhiteSpace(IncomingServerPassword)
           && !string.IsNullOrWhiteSpace(OutgoingServer)
           && !string.IsNullOrWhiteSpace(OutgoingServerPort)
           && !string.IsNullOrWhiteSpace(OutgoingServerUsername)
           && !string.IsNullOrWhiteSpace(OutgoingServerPassword)
           && IsValidPort(IncomingServerPort)
           && IsValidPort(OutgoingServerPort));

    private int FindAuthenticationMethodIndex(ImapAuthenticationMethod method)
    {
        var index = AvailableAuthenticationMethods.FindIndex(a => a.ImapAuthenticationMethod == method);
        return index < 0 ? 0 : index;
    }

    private int FindConnectionSecurityIndex(ImapConnectionSecurity security)
    {
        var index = AvailableConnectionSecurities.FindIndex(a => a.ImapConnectionSecurity == security);
        return index < 0 ? 0 : index;
    }

    private ImapAuthenticationMethod GetAuthenticationMethodByIndex(int index)
    {
        if (index < 0 || index >= AvailableAuthenticationMethods.Count)
            return ImapAuthenticationMethod.Auto;

        return AvailableAuthenticationMethods[index].ImapAuthenticationMethod;
    }

    private ImapConnectionSecurity GetConnectionSecurityByIndex(int index)
    {
        if (index < 0 || index >= AvailableConnectionSecurities.Count)
            return ImapConnectionSecurity.Auto;

        return AvailableConnectionSecurities[index].ImapConnectionSecurity;
    }
}
