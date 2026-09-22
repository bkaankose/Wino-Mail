using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Telemetry;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The guided flow of the IMAP server page: sign in, then check the servers. Also carries the
/// connection states and the capability choices that the edit page shares with the wizard.
/// </summary>
public partial class ImapCalDavSettingsPageViewModel
{
    private AccountCapabilityMode _initialContactMode = AccountCapabilityMode.Off;
    private AccountCapabilityMode _initialTaskMode = AccountCapabilityMode.Off;
    private string _initialAccountName = string.Empty;
    private bool _isSyncingCapabilities;

    // Discovery runs again only for a different address, so going back to fix a password keeps
    // the server values the user already edited.
    private string _lastDiscoveredAddress;

    /// <summary>
    /// What this account is used for. Editable on the edit page through the capability picker;
    /// while adding an account it was chosen on the provider page and only drives the layout.
    /// </summary>
    public AccountCapabilitySelection Capabilities { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsServersStepVisible))]
    [NotifyPropertyChangedFor(nameof(IsSignInStepCurrent))]
    [NotifyPropertyChangedFor(nameof(IsServersStepCurrent))]
    [NotifyPropertyChangedFor(nameof(IsSignInStepDone))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(IsDiscoveryFoundVisible))]
    [NotifyPropertyChangedFor(nameof(IsDiscoveryNotFoundVisible))]
    public partial ImapSetupStep CurrentSetupStep { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscoveryFoundVisible))]
    [NotifyPropertyChangedFor(nameof(IsDiscoveryNotFoundVisible))]
    [NotifyPropertyChangedFor(nameof(DiscoveryFoundMessage))]
    [NotifyPropertyChangedFor(nameof(DiscoveryNotFoundMessage))]
    public partial ServerDiscoveryState DiscoveryState { get; set; }

    /// <summary>
    /// One state for incoming and outgoing mail: the server test signs in to both in one pass.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMailConnectionError))]
    public partial ConnectionTestState MailConnectionState { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMailConnectionError))]
    public partial string MailConnectionError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalDavConnectionError))]
    public partial ConnectionTestState CalDavConnectionState { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalDavConnectionError))]
    public partial string CalDavConnectionError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsIncomingExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsOutgoingExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsCalDavExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsCardDavExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutgoingCredentialsVisible))]
    public partial bool UseIncomingCredentialsForOutgoing { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDavCredentialsVisible))]
    [NotifyPropertyChangedFor(nameof(IsCardDavCredentialsVisible))]
    public partial bool UseMailCredentialsForDav { get; set; } = true;

    [ObservableProperty]
    public partial string AppPasswordHelpText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppPasswordHelpLink))]
    [NotifyCanExecuteChangedFor(nameof(OpenAppPasswordHelpCommand))]
    public partial string AppPasswordHelpUrl { get; set; } = string.Empty;

    public bool IsNotBusy => !IsBusy;

    public bool IsSignInStepVisible => IsCreateMode && CurrentSetupStep == ImapSetupStep.SignIn;
    public bool IsServersStepVisible => IsCreateMode
        ? CurrentSetupStep == ImapSetupStep.Servers
        : Capabilities.RequiresRemoteService;

    public bool IsSignInStepCurrent => CurrentSetupStep == ImapSetupStep.SignIn;
    public bool IsServersStepCurrent => CurrentSetupStep == ImapSetupStep.Servers;
    public bool IsSignInStepDone => CurrentSetupStep == ImapSetupStep.Servers;

    public bool IsEditSignInVisible => IsEditMode && Capabilities.RequiresRemoteService;

    public string PrimaryActionText => IsEditMode
        ? Translator.Buttons_Save
        : CurrentSetupStep == ImapSetupStep.SignIn
            ? Translator.ProviderSelection_ContinueButton
            : Translator.ProviderSelection_AddAccountButton;

    public bool IsDiscoveryFoundVisible => IsCreateMode && IsServersStepCurrent && DiscoveryState == ServerDiscoveryState.Found;
    public bool IsDiscoveryNotFoundVisible => IsCreateMode && IsServersStepCurrent && DiscoveryState == ServerDiscoveryState.NotFound;
    public string DiscoveryFoundMessage => string.Format(Translator.ImapSetup_DiscoveryFound, AddressDomain);
    public string DiscoveryNotFoundMessage => string.Format(Translator.ImapSetup_DiscoveryNotFound, AddressDomain);

    public bool IsSignInSenderNameVisible => IsMailSupportEnabled;
    public string SignInTitle => IsMailSupportEnabled ? Translator.ImapSetup_SignInMailTitle : Translator.ImapSetup_SignInDavTitle;
    public string SignInDescription => IsMailSupportEnabled ? Translator.ImapSetup_SignInMailDescription : Translator.ImapSetup_SignInDavDescription;
    public string SignInAddressHeader => IsMailSupportEnabled ? Translator.IMAPSetupDialog_MailAddress : Translator.ImapSetup_AddressOrUserName;

    /// <summary>
    /// Without mail there is no domain to discover from reliably, so the CalDAV address can be
    /// typed on the sign-in step. It stays optional: discovery still tries the address domain.
    /// </summary>
    public bool IsSignInServerAddressVisible => !IsMailSupportEnabled && IsCalDavSettingsVisible;

    public bool IsOutgoingCredentialsVisible => !UseIncomingCredentialsForOutgoing;
    public bool IsDavCredentialsOptionVisible => IsMailSupportEnabled;
    public bool IsDavCredentialsVisible => IsMailSupportEnabled && !UseMailCredentialsForDav;

    // With no calendar row, the contacts row carries the shared DAV sign-in option.
    public bool IsCardDavCredentialsOptionVisible => IsCardDavOnlySettingsVisible && IsMailSupportEnabled;
    public bool IsCardDavCredentialsVisible => IsCardDavCredentialsOptionVisible && !UseMailCredentialsForDav;

    /// <summary>
    /// NumberBox works in doubles; the server setting is a whole number of connections.
    /// </summary>
    public double MaxConcurrentClientsValue
    {
        get => MaxConcurrentClients;
        set => MaxConcurrentClients = double.IsNaN(value) ? 5 : (int)Math.Clamp(Math.Round(value), 1, 20);
    }

    public bool HasMailConnectionError => MailConnectionState == ConnectionTestState.Failed && !string.IsNullOrWhiteSpace(MailConnectionError);
    public bool HasCalDavConnectionError => CalDavConnectionState == ConnectionTestState.Failed && !string.IsNullOrWhiteSpace(CalDavConnectionError);
    public bool HasAppPasswordHelpLink => !string.IsNullOrWhiteSpace(AppPasswordHelpUrl);

    public bool IsMailAdvancedOptionsVisible => IsMailSupportEnabled;

    public string IncomingSummary => DescribeEndpoint(IncomingServer, IncomingServerPort, SelectedIncomingServerConnectionSecurityIndex);
    public string OutgoingSummary => DescribeEndpoint(OutgoingServer, OutgoingServerPort, SelectedOutgoingServerConnectionSecurityIndex);
    public string CalDavSummary => string.IsNullOrWhiteSpace(CalDavServiceUrl) ? Translator.ImapSetup_ServerNotSet : CalDavServiceUrl.Trim();
    public string CardDavSummary => string.IsNullOrWhiteSpace(CardDavServiceUrl) ? Translator.ImapSetup_CardDavAutomatic : CardDavServiceUrl.Trim();

    private string EffectiveOutgoingUsername => UseIncomingCredentialsForOutgoing ? IncomingServerUsername : OutgoingServerUsername;
    private string EffectiveOutgoingPassword => UseIncomingCredentialsForOutgoing ? IncomingServerPassword : OutgoingServerPassword;

    // Without mail the sign-in step is the DAV sign-in, so the DAV fields are used as they are.
    private string EffectiveDavUsername => IsMailSupportEnabled && UseMailCredentialsForDav ? IncomingServerUsername : CalDavUsername;
    private string EffectiveDavPassword => IsMailSupportEnabled && UseMailCredentialsForDav ? IncomingServerPassword : CalDavPassword;

    private string AddressDomain => MailAccountAddressValidator.TryGetDomain(EmailAddress?.Trim(), out var domain)
        ? domain
        : EmailAddress?.Trim() ?? string.Empty;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task PrimaryActionAsync()
    {
        if (IsCreateMode && CurrentSetupStep == ImapSetupStep.SignIn)
        {
            await ContinueToServersAsync();
            return;
        }

        await SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void Back()
    {
        if (IsCreateMode && CurrentSetupStep == ImapSetupStep.Servers)
        {
            CurrentSetupStep = ImapSetupStep.SignIn;
            return;
        }

        // The first step goes back to the provider page, where the capabilities were chosen.
        Cancel();
    }

    private async Task ContinueToServersAsync()
    {
        await HidePageErrorAsync();

        IsBusy = true;
        BusyText = string.Format(Translator.ImapSetup_LookingUp, AddressDomain);

        try
        {
            ValidateSignIn();

            var normalizedAddress = EmailAddress.Trim();
            if (!string.Equals(_lastDiscoveredAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase))
            {
                var isFound = await DiscoverServerSettingsAsync();

                _lastDiscoveredAddress = normalizedAddress;
                DiscoveryState = isFound ? ServerDiscoveryState.Found : ServerDiscoveryState.NotFound;
                ResetConnectionStates();

                // What discovery could not fill in opens so it can be typed.
                IsIncomingExpanded = !isFound && IsMailSupportEnabled;
                IsOutgoingExpanded = !isFound && IsMailSupportEnabled;
                IsCalDavExpanded = IsCalDavSettingsVisible && string.IsNullOrWhiteSpace(CalDavServiceUrl);
            }

            CurrentSetupStep = ImapSetupStep.Servers;
        }
        catch (Exception ex)
        {
            await ShowImapValidationFailureAsync(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task TestConnectionsAsync()
    {
        await HidePageErrorAsync();

        IsBusy = true;
        BusyText = Translator.ImapSetup_TestingConnections;

        try
        {
            if (IsMailSupportEnabled)
            {
                try
                {
                    await EnsureImapSettingsPreparedAsync();

                    var serverInformation = BuildServerInformationForTest();
                    ValidateImapSettings(serverInformation);
                    await TestMailConnectionAsync(serverInformation);
                }
                catch (Exception ex)
                {
                    SetMailConnectionFailed(ex);
                }
            }

            if (IsCalDavSettingsVisible)
            {
                try
                {
                    TryApplyKnownProviderSettingsIfNeeded(requireCompleteImapSettings: false, requireCompleteCalDavSettings: true);

                    var serverInformation = BuildServerInformationForTest();
                    await TestCalDavConnectionAsync(serverInformation);
                }
                catch (Exception ex)
                {
                    SetCalDavConnectionFailed(ex);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasAppPasswordHelpLink))]
    private async Task OpenAppPasswordHelpAsync()
    {
        if (_nativeAppService == null || !Uri.TryCreate(AppPasswordHelpUrl, UriKind.Absolute, out var uri))
            return;

        await _nativeAppService.LaunchUriAsync(uri);
    }

    private async Task TestMailConnectionAsync(CustomServerInformation serverInformation)
    {
        MailConnectionState = ConnectionTestState.Testing;
        MailConnectionError = string.Empty;

        try
        {
            await ValidateImapConnectivityAsync(serverInformation);

            MailConnectionState = ConnectionTestState.Succeeded;
            IsImapValidationSucceeded = true;

            TrackImapSetupEvent("imap_connection_test_completed", result: "success", serverInformation: serverInformation);
        }
        catch (Exception ex)
        {
            SetMailConnectionFailed(ex);

            TrackImapSetupEvent(
                "imap_connection_test_completed",
                result: "failure",
                failureStage: "imap_connection_test",
                failureCategory: ClassifySetupFailure(ex),
                exception: ex,
                serverInformation: serverInformation,
                level: WinoTelemetryLevel.Warning);

            throw;
        }
    }

    private async Task TestCalDavConnectionAsync(CustomServerInformation serverInformation)
    {
        CalDavConnectionState = ConnectionTestState.Testing;
        CalDavConnectionError = string.Empty;

        try
        {
            await ValidateCalDavConnectivityAsync(serverInformation);

            CalDavConnectionState = ConnectionTestState.Succeeded;
            IsCalDavValidationSucceeded = true;

            TrackImapSetupEvent("caldav_test_completed", result: "success", serverInformation: serverInformation);
        }
        catch (Exception ex)
        {
            SetCalDavConnectionFailed(ex);

            TrackImapSetupEvent(
                "caldav_test_completed",
                result: "failure",
                failureStage: "caldav_test",
                failureCategory: ClassifySetupFailure(ex),
                exception: ex,
                serverInformation: serverInformation,
                level: WinoTelemetryLevel.Warning);

            throw;
        }
    }

    private void SetMailConnectionFailed(Exception exception)
    {
        MailConnectionState = ConnectionTestState.Failed;
        MailConnectionError = exception.Message;
        IsImapValidationSucceeded = false;
        IsIncomingExpanded = true;
    }

    private void SetCalDavConnectionFailed(Exception exception)
    {
        CalDavConnectionState = ConnectionTestState.Failed;
        CalDavConnectionError = exception.Message;
        IsCalDavValidationSucceeded = false;
        IsCalDavExpanded = true;
    }

    private CustomServerInformation BuildServerInformationForTest()
    {
        var serverInformation = BuildServerInformation();

        if (_pageMode == ImapCalDavSettingsPageMode.Edit)
            serverInformation.AccountId = _editingAccountId;

        return serverInformation;
    }

    private void ValidateSignIn()
    {
        ValidateIdentitySettings();

        if (string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException(Translator.IMAPAdvancedSetupDialog_ValidationPasswordRequired);
    }

    /// <summary>
    /// Fills the server fields from the provider catalog or from autodiscovery. Returns false
    /// when a required server could not be found, so the page opens those rows for typing.
    /// </summary>
    private async Task<bool> DiscoverServerSettingsAsync()
    {
        var isMailFound = true;
        var isCalendarFound = true;

        if (IsMailSupportEnabled)
        {
            try
            {
                await AutoDiscoverAndApplySettingsAsync(BuildMinimalSettingsOrThrow());
            }
            catch (Exception ex)
            {
                isMailFound = false;

                TrackImapSetupEvent(
                    "imap_autodiscovery_completed",
                    result: "failure",
                    failureStage: "autodiscovery",
                    failureCategory: ClassifySetupFailure(ex),
                    exception: ex,
                    level: WinoTelemetryLevel.Warning);
            }
        }

        if (IsCalDavSettingsVisible && string.IsNullOrWhiteSpace(CalDavServiceUrl))
        {
            try
            {
                var discoveredUri = await _autoDiscoveryService.DiscoverCalDavServiceUriAsync(EmailAddress.Trim());
                if (discoveredUri != null)
                    CalDavServiceUrl = discoveredUri.ToString();
            }
            catch (Exception)
            {
                // A missing CalDAV address is reported through the result, not as an error.
            }

            isCalendarFound = !string.IsNullOrWhiteSpace(CalDavServiceUrl);
        }

        if (isMailFound && isCalendarFound)
            TrackImapSetupEvent("imap_autodiscovery_completed", result: "success", serverInformation: TryBuildServerInformationForTelemetry());

        return isMailFound && isCalendarFound;
    }

    private void ResetSetupProgress()
    {
        CurrentSetupStep = ImapSetupStep.SignIn;
        DiscoveryState = ServerDiscoveryState.NotRun;
        _lastDiscoveredAddress = null;
        ResetConnectionStates();
        IsIncomingExpanded = false;
        IsOutgoingExpanded = false;
        IsCalDavExpanded = false;
        IsCardDavExpanded = false;
    }

    private void ResetConnectionStates()
    {
        MailConnectionState = ConnectionTestState.NotTested;
        MailConnectionError = string.Empty;
        CalDavConnectionState = ConnectionTestState.NotTested;
        CalDavConnectionError = string.Empty;
    }

    /// <summary>
    /// Copies the page's current mail, calendar, contact and task state into the capability model
    /// the picker edits. While adding an account the wizard context supplies contacts and tasks.
    /// </summary>
    private void SyncCapabilitiesFromState(AccountCreationDialogResult creationResult)
    {
        _isSyncingCapabilities = true;

        try
        {
            Capabilities.MailDescription = IsPop3 ? Translator.ImapSetup_MailDescriptionPop3 : Translator.ImapSetup_MailDescriptionImap;
            Capabilities.ConfigureProvider(
                isCalendarProviderAvailable: !IsPop3,
                isContactProviderAvailable: !IsPop3,
                isTaskProviderAvailable: false,
                calendarProviderName: Translator.ProviderSelection_SourceCalDav,
                contactProviderName: Translator.ProviderSelection_SourceCardDav,
                taskProviderName: string.Empty);

            Capabilities.MailMode = IsMailSupportEnabled ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
            Capabilities.CalendarMode = !IsCalendarSupportEnabled || SelectedCalendarSupportMode == ImapCalendarSupportMode.Disabled
                ? AccountCapabilityMode.Off
                : SelectedCalendarSupportMode == ImapCalendarSupportMode.CalDav
                    ? AccountCapabilityMode.Provider
                    : AccountCapabilityMode.Local;

            if (IsCreateMode)
            {
                Capabilities.ContactMode = !(creationResult?.IsContactAccessGranted ?? _wizardContext.IsContactAccessEnabled)
                    ? AccountCapabilityMode.Off
                    : _isCardDavEnabled ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
                Capabilities.TaskMode = (creationResult?.IsTaskAccessGranted ?? _wizardContext.IsTaskAccessEnabled)
                    ? AccountCapabilityMode.Local
                    : AccountCapabilityMode.Off;
            }
            else
            {
                Capabilities.ContactMode = _initialContactMode;
                Capabilities.TaskMode = _initialTaskMode;
            }

            Capabilities.CoerceUnavailableModes();

            // The contact mode decides CardDAV, not the integration source alone.
            _isCardDavEnabled = Capabilities.ContactMode == AccountCapabilityMode.Provider;
        }
        finally
        {
            _isSyncingCapabilities = false;
        }
    }

    private void OnCapabilitiesPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isSyncingCapabilities)
            return;

        switch (e.PropertyName)
        {
            case nameof(AccountCapabilitySelection.MailMode):
                IsMailSupportEnabled = Capabilities.IsMailEnabled;
                break;
            case nameof(AccountCapabilitySelection.CalendarMode):
                ApplyCalendarModeFromCapabilities();
                break;
            case nameof(AccountCapabilitySelection.ContactMode):
                _isCardDavEnabled = Capabilities.ContactMode == AccountCapabilityMode.Provider;
                OnPropertyChanged(nameof(IsCardDavSettingsVisible));
                OnPropertyChanged(nameof(IsCardDavOnlySettingsVisible));
                break;
            default:
                return;
        }

        NotifySetupLayoutChanged();
    }

    private void ApplyCalendarModeFromCapabilities()
    {
        switch (Capabilities.CalendarMode)
        {
            case AccountCapabilityMode.Provider:
                IsCalendarSupportEnabled = true;
                SelectedCalendarSupportMode = ImapCalendarSupportMode.CalDav;
                break;
            case AccountCapabilityMode.Local:
                IsCalendarSupportEnabled = true;
                SelectedCalendarSupportMode = ImapCalendarSupportMode.LocalOnly;
                break;
            default:
                IsCalendarSupportEnabled = false;
                break;
        }
    }

    private void NotifySetupLayoutChanged()
    {
        OnPropertyChanged(nameof(IsSignInStepVisible));
        OnPropertyChanged(nameof(IsServersStepVisible));
        OnPropertyChanged(nameof(IsEditSignInVisible));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(IsSignInSenderNameVisible));
        OnPropertyChanged(nameof(SignInTitle));
        OnPropertyChanged(nameof(SignInDescription));
        OnPropertyChanged(nameof(SignInAddressHeader));
        OnPropertyChanged(nameof(IsSignInServerAddressVisible));
        OnPropertyChanged(nameof(IsDavCredentialsOptionVisible));
        OnPropertyChanged(nameof(IsDavCredentialsVisible));
        OnPropertyChanged(nameof(IsMailAdvancedOptionsVisible));
        OnPropertyChanged(nameof(IsDiscoveryFoundVisible));
        OnPropertyChanged(nameof(IsDiscoveryNotFoundVisible));
        OnPropertyChanged(nameof(IsCalDavSettingsVisible));
        OnPropertyChanged(nameof(IsCardDavSettingsVisible));
        OnPropertyChanged(nameof(IsCardDavOnlySettingsVisible));
        OnPropertyChanged(nameof(IsCardDavCredentialsOptionVisible));
        OnPropertyChanged(nameof(IsCardDavCredentialsVisible));
        OnPropertyChanged(nameof(IncomingSettingsTitle));
    }

    partial void OnMaxConcurrentClientsChanged(int value) => OnPropertyChanged(nameof(MaxConcurrentClientsValue));

    private void UpdateAppPasswordHelp()
    {
        var help = _knownImapProviderCatalog?.FindAppPasswordHelp(EmailAddress);

        AppPasswordHelpText = help == null
            ? Translator.ImapSetup_AppPasswordGenericHint
            : string.Format(Translator.ImapSetup_AppPasswordProviderHint, help.ProviderName);
        AppPasswordHelpUrl = help?.HelpUrl ?? string.Empty;
    }

    private string DescribeEndpoint(string host, string port, int securityIndex)
    {
        if (string.IsNullOrWhiteSpace(host))
            return Translator.ImapSetup_ServerNotSet;

        var security = securityIndex >= 0 && securityIndex < AvailableConnectionSecurityDisplayNames.Count
            ? AvailableConnectionSecurityDisplayNames[securityIndex]
            : string.Empty;

        return string.Join(" · ", new[] { host.Trim(), port?.Trim(), security }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsSameCredential(string username, string password, string referenceUsername, string referencePassword)
    {
        var isUsernameShared = string.IsNullOrWhiteSpace(username) ||
                               string.Equals(username.Trim(), referenceUsername?.Trim(), StringComparison.OrdinalIgnoreCase);
        var isPasswordShared = string.IsNullOrEmpty(password) || string.Equals(password, referencePassword, StringComparison.Ordinal);

        return isUsernameShared && isPasswordShared;
    }

    partial void OnEmailAddressChanged(string value)
    {
        UpdateAppPasswordHelp();
        OnPropertyChanged(nameof(DiscoveryFoundMessage));
        OnPropertyChanged(nameof(DiscoveryNotFoundMessage));
    }

    partial void OnIsMailSupportEnabledChanged(bool oldValue, bool newValue) => NotifySetupLayoutChanged();

    partial void OnIncomingServerChanged(string value) => OnPropertyChanged(nameof(IncomingSummary));
    partial void OnIncomingServerPortChanged(string value) => OnPropertyChanged(nameof(IncomingSummary));
    partial void OnSelectedIncomingServerConnectionSecurityIndexChanged(int value) => OnPropertyChanged(nameof(IncomingSummary));
    partial void OnOutgoingServerChanged(string value) => OnPropertyChanged(nameof(OutgoingSummary));
    partial void OnOutgoingServerPortChanged(string value) => OnPropertyChanged(nameof(OutgoingSummary));
    partial void OnSelectedOutgoingServerConnectionSecurityIndexChanged(int value) => OnPropertyChanged(nameof(OutgoingSummary));
    partial void OnCalDavServiceUrlChanged(string value) => OnPropertyChanged(nameof(CalDavSummary));
    partial void OnCardDavServiceUrlChanged(string value) => OnPropertyChanged(nameof(CardDavSummary));
}
