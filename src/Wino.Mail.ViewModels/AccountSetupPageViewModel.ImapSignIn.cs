#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Helpers;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The IMAP route's sign-in: the options from the resolution in fixed order, password checks on Continue or Enter only,
/// browser sign-in through <see cref="IOAuthTokenService"/>, then the connection checks behind one status line.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private OAuthSetupSession? _oauthSession;
    private IReadOnlyCollection<string> _grantedScopes = Array.Empty<string>();
    private CustomServerInformation? _pendingServer;
    private string? _failureProtocolLog;
    private AccountSetupRetryAction _retryAction;
    private bool _isSignInValidated;
    private readonly List<AccountSetupConnectionCheck> _observedChecks = [];

    /// <summary>
    /// The sign-in finished and every connection check passed; the sign-in step stays on screen and Add account is
    /// enabled. Editing the credential, signing in again or leaving the step clears it.
    /// </summary>
    public bool IsSignInValidated => _isSignInValidated;

    public ObservableCollection<AccountSetupConnectionCheck> Checks { get; } = [];

    public List<AccountSetupImapOption> ImapOptions { get; private set; } = [];

    public List<KnownImapProviderRegion> Regions { get; private set; } = [];

    [ObservableProperty]
    public partial AccountSetupImapOption? SelectedImapOption { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckPasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckPasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string LoginAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial KnownImapProviderRegion? SelectedRegion { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithBrowserCommand))]
    public partial bool IsIssuerConfirmed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDavPasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial bool IsDavPasswordRequired { get; set; }

    [ObservableProperty]
    public partial string DavUserName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDavPasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string DavPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInCaptionVisible))]
    [NotifyPropertyChangedFor(nameof(IsSetupHintCaptionVisible))]
    public partial bool IsFailureOpen { get; set; }

    [ObservableProperty]
    public partial string FailureTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FailureMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarMessageType FailureSeverity { get; set; } = InfoBarMessageType.Error;

    public bool IsSignInBlockVisible => !IsLocalOnlyMode &&
                                        Capabilities.RequiresRemoteService &&
                                        Phase is AccountSetupPhase.SignIn or AccountSetupPhase.Checking;

    public bool IsImapSignInVisible => IsSignInBlockVisible && Route == AccountRoute.Imap;
    public string SelectedImapOptionDescription => SelectedImapOption?.Description ?? string.Empty;

    public bool IsBrowserPanelVisible => IsImapSignInVisible && SelectedImapOption?.IsBrowser == true;
    public bool IsPasswordPanelVisible => IsImapSignInVisible && SelectedImapOption?.IsPassword == true;

    public string BrowserSignInButtonText => OAuthState == AccountSetupOAuthState.SignedIn
        ? Translator.ImapSetup_OAuthSignInAgain
        : SelectedOAuthDiscovery?.Profile?.TrustSource == OAuthTrustSource.BuiltIn && !string.IsNullOrWhiteSpace(ProviderLabel)
            ? string.Format(Translator.ImapSetup_SignInWithProvider, ProviderLabel)
            : Translator.ImapSetup_SignInWithOrganization;

    /// <summary>After a browser sign-in the button stays only while the sign-in still needs attention.</summary>
    public bool IsBrowserSignInButtonVisible => IsBrowserPanelVisible &&
                                                (OAuthState != AccountSetupOAuthState.SignedIn ||
                                                 (Phase == AccountSetupPhase.SignIn && !IsSignInValidated));

    public bool IsIssuerConfirmationVisible => IsBrowserPanelVisible &&
                                               SelectedOAuthDiscovery?.RequiresIssuerConfirmation == true &&
                                               OAuthState != AccountSetupOAuthState.SignedIn;

    public string IssuerConfirmationText => string.Format(Translator.ImapSetup_OAuthConfirmIssuer, IssuerHost);

    public string PasswordHeader => SelectedImapOption?.Option.PasswordKind switch
    {
        KnownImapPasswordKind.BridgePassword => Translator.ProviderSelection_PasswordHeader_Bridge,
        KnownImapPasswordKind.AppPassword => Translator.AccountSetup_Option_AppPassword,
        _ => Translator.ProviderSelection_PasswordHeader_Account
    };

    /// <summary>The provider's setup hint above a password field; a failure InfoBar takes its place.</summary>
    public string SignInCaption => IsPasswordPanelVisible ? SetupHintText : string.Empty;

    public bool IsSignInCaptionVisible => !IsFailureOpen &&
                                          !string.IsNullOrWhiteSpace(SignInCaption) &&
                                          (IsNativeSignInButtonVisible || IsBrowserSignInButtonVisible || IsPasswordPanelVisible);

    public bool IsSetupHintCaptionVisible => IsSignInCaptionVisible && IsPasswordPanelVisible;

    public string SetupHintText => GetSetupHintText();
    public string HelpUrl => SelectedImapOption?.Option.HelpUrl ?? string.Empty;
    public bool IsHelpLinkVisible => IsPasswordPanelVisible && !string.IsNullOrWhiteSpace(HelpUrl);

    /// <summary>The help link names what the provider's page explains: an app password, or turning IMAP on.</summary>
    public string HelpLinkText => SelectedImapOption?.Option switch
    {
        { Kind: ImapSignInOptionKind.AppPassword } => Translator.ImapSetup_AppPasswordHelpLink,
        { SetupHint: KnownImapSetupHint.ImapDisabledByDefault } => Translator.ImapSetup_TurnOnImapHelpLink,
        _ => Translator.ImapSetup_ProviderHelpLink
    };

    public bool IsRegionVisible => IsPasswordPanelVisible && Regions.Count > 0;
    public string RegionDescription => string.Format(Translator.ProviderSelection_RegionDescription, ProviderLabel);

    public bool IsLoginAddressVisible => IsPasswordPanelVisible && SelectedImapOption?.Option.RequiresLoginAddress == true;
    public string LoginAddressHeader => string.Format(Translator.ProviderSelection_LoginAddressHeader, ProviderLabel);
    public string LoginAddressDescription => string.Format(Translator.ProviderSelection_CustomDomain_Message, AddressDomain, ProviderLabel, EmailAddress?.Trim() ?? string.Empty);

    public bool IsDavPasswordVisible => IsSignInBlockVisible && IsDavPasswordRequired;
    public bool IsUseAnotherOptionVisible => IsImapSignInVisible && ImapOptions.Count > 1;
    public bool IsRetryVisible => _retryAction != AccountSetupRetryAction.None;
    public bool HasFailureDetails => !string.IsNullOrWhiteSpace(_failureProtocolLog);

    public AccountSetupConnectionStatus ConnectionStatusKind
    {
        get
        {
            if (IsSignInValidated && Phase == AccountSetupPhase.SignIn)
                return AccountSetupConnectionStatus.Connected;

            if (!IsSignInBlockVisible)
                return AccountSetupConnectionStatus.None;

            if (Phase == AccountSetupPhase.Checking || Checks.Any(check => check.IsTesting))
                return AccountSetupConnectionStatus.Checking;

            if (IsOAuthWaiting)
                return AccountSetupConnectionStatus.Waiting;

            if (Checks.Any(check => check.IsFailed))
                return AccountSetupConnectionStatus.Failed;

            return Checks.Any(check => check.NeedsPassword)
                ? AccountSetupConnectionStatus.NeedsInput
                : AccountSetupConnectionStatus.None;
        }
    }

    /// <summary>The status line under the sign-in: waiting, checking, then signed in. Failures use the InfoBar instead.</summary>
    public bool IsConnectionStatusVisible => Step == AccountSetupStep.SignIn &&
                                             ConnectionStatusKind is AccountSetupConnectionStatus.Waiting or AccountSetupConnectionStatus.Checking or AccountSetupConnectionStatus.Connected;
    public bool IsConnectionStatusBusy => ConnectionStatusKind is AccountSetupConnectionStatus.Waiting or AccountSetupConnectionStatus.Checking;
    public bool IsConnectionStatusConnected => ConnectionStatusKind == AccountSetupConnectionStatus.Connected;

    public string ConnectionStatusText => ConnectionStatusKind switch
    {
        AccountSetupConnectionStatus.Waiting => string.Format(Translator.AccountSetup_Status_Waiting, SignInPartyName),
        AccountSetupConnectionStatus.Checking => Translator.AccountSetup_Status_Checking,
        AccountSetupConnectionStatus.NeedsInput => Translator.AccountSetup_Status_DavPasswordNeeded,
        AccountSetupConnectionStatus.Failed => Checks.FirstOrDefault(check => check.IsFailed && check.HasMessage)?.Message
                                               ?? Translator.AccountSetup_Failure_Title,
        AccountSetupConnectionStatus.Connected => IsNativeRoute ? SignedInTitle : Translator.AccountSetup_Status_AllConnected,
        _ => string.Empty
    };

    private string SignInPartyName => Route switch
    {
        AccountRoute.Gmail => "Google",
        AccountRoute.Outlook => "Microsoft",
        AccountRoute.Exchange => ExchangeAuthorityHost,
        _ => SelectedOAuthDiscovery?.Profile?.TrustSource == OAuthTrustSource.BuiltIn && !string.IsNullOrWhiteSpace(ProviderLabel)
            ? ProviderLabel
            : string.IsNullOrWhiteSpace(IssuerHost) ? ProviderLabel : IssuerHost
    };

    private OAuthDiscoveryResult? SelectedOAuthDiscovery => SelectedImapOption?.Option.OAuthDiscovery;

    private string IssuerHost
    {
        get
        {
            var discovery = SelectedOAuthDiscovery;
            var discoveredHost = discovery?.IssuerHost;
            if (!string.IsNullOrWhiteSpace(discoveredHost))
                return discoveredHost;

            var issuer = _oauthSession?.Profile.Issuer ?? discovery?.Profile?.Issuer;
            return Uri.TryCreate(issuer, UriKind.Absolute, out var uri) ? uri.Host : issuer ?? string.Empty;
        }
    }

    private string ProviderLabel
    {
        get
        {
            var name = _resolution?.Imap?.Provider?.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = _resolution?.ProviderDisplayName;

            return string.IsNullOrWhiteSpace(name) ? ImapHostName : name;
        }
    }

    private bool CanCheckPassword() => Phase == AccountSetupPhase.SignIn &&
                                       SelectedImapOption is { IsPassword: true } option &&
                                       !string.IsNullOrEmpty(Password) &&
                                       (!option.Option.RequiresLoginAddress || MailAccountAddressValidator.IsValid(LoginAddress?.Trim() ?? string.Empty));

    [RelayCommand(CanExecute = nameof(CanCheckPassword))]
    private Task CheckPasswordAsync() => RunImapChecksAsync(useOAuth: false);

    private bool CanSignInWithBrowser() => Phase == AccountSetupPhase.SignIn &&
                                           SelectedImapOption?.IsBrowser == true &&
                                           SelectedOAuthDiscovery is { Profile: not null } discovery &&
                                           _oauthTokenService != null &&
                                           OAuthState != AccountSetupOAuthState.Waiting &&
                                           (!discovery.RequiresIssuerConfirmation || IsIssuerConfirmed);

    /// <summary>
    /// Signs in on the provider's page with the scopes of the chosen modes, then checks the connections by itself.
    /// A client discovery left unregistered registers here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignInWithBrowser))]
    private async Task SignInWithBrowserAsync()
    {
        var tokenService = _oauthTokenService;
        var discovery = SelectedOAuthDiscovery;
        var profile = discovery?.Profile;
        if (tokenService == null || discovery == null || profile == null)
            return;

        var operation = StartOperation();
        HideFailure();
        DiscardOAuthSession();
        ResetChecks();
        SetSignInValidated(false);

        Phase = AccountSetupPhase.SignIn;
        OAuthState = AccountSetupOAuthState.Waiting;

        OAuthSetupSession session;

        try
        {
            var registeredScopes = string.IsNullOrWhiteSpace(profile.ClientId) ? null : OAuthScopes.Split(profile.Scopes);
            var prepared = OAuthSignInProfile.PrepareForModes(
                profile,
                discovery.SupportedScopes,
                registeredScopes,
                IsCalendarRemote,
                IsContactRemote,
                _providerCatalog.GetOAuthDavSettings(profile));

            session = await tokenService.SignInAsync(prepared, EmailAddress?.Trim(), operation.Token);

            if (operation.IsCancellationRequested)
            {
                tokenService.DiscardSession(session.Key);
                return;
            }

            _oauthSession = session;
            _grantedScopes = await ReadGrantedScopesAsync(tokenService, session.Key);
            OAuthState = AccountSetupOAuthState.SignedIn;
        }
        catch (OAuthSignInException ex) when (!operation.IsCancellationRequested)
        {
            OAuthState = AccountSetupOAuthState.Idle;
            ShowOAuthFailure(ex.Kind, ex.Message);
            return;
        }
        catch (OperationCanceledException) when (!operation.IsCancellationRequested)
        {
            OAuthState = AccountSetupOAuthState.Idle;
            ShowOAuthFailure(OAuthSignInFailureKind.Cancelled, null);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (operation.IsCancellationRequested)
                return;

            Log.Warning(ex, "Browser sign-in failed during account setup.");
            OAuthState = AccountSetupOAuthState.Idle;
            ShowOAuthFailure(OAuthSignInFailureKind.Failed, ex.Message);
            return;
        }

        await RunImapChecksAsync(useOAuth: true);
    }

    private bool CanCheckDavPassword() => Phase == AccountSetupPhase.SignIn &&
                                          IsDavPasswordRequired &&
                                          _pendingServer != null &&
                                          !string.IsNullOrEmpty(DavPassword);

    [RelayCommand(CanExecute = nameof(CanCheckDavPassword))]
    private async Task CheckDavPasswordAsync()
    {
        var server = _pendingServer;
        if (server == null)
            return;

        var operation = StartOperation();
        HideFailure();
        SetSignInValidated(false);
        Phase = AccountSetupPhase.Checking;

        foreach (var check in Checks.Where(check => check.Kind is AccountSetupCheckKind.Calendar or AccountSetupCheckKind.Contacts))
            ResetCheck(check);

        await RunDavChecksAsync(server, operation);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        switch (_retryAction)
        {
            case AccountSetupRetryAction.NativeSignIn:
                await SignInWithProviderAsync();
                break;
            case AccountSetupRetryAction.BrowserSignIn when _oauthSession != null:
                await RunImapChecksAsync(useOAuth: true);
                break;
            case AccountSetupRetryAction.BrowserSignIn:
                if (CanSignInWithBrowser())
                    await SignInWithBrowserAsync();
                break;
            case AccountSetupRetryAction.PasswordCheck:
                if (CanCheckPassword())
                    await RunImapChecksAsync(useOAuth: false);
                break;
            case AccountSetupRetryAction.ExchangePasswordCheck:
                if (CanCheckExchangePassword())
                    await CheckExchangePasswordAsync();
                break;
            case AccountSetupRetryAction.DavCheck:
                if (_pendingServer != null)
                {
                    var operation = StartOperation();
                    HideFailure();
                    SetSignInValidated(false);
                    Phase = AccountSetupPhase.Checking;
                    await RunDavChecksAsync(_pendingServer, operation);
                }
                break;
        }
    }

    [RelayCommand]
    private void UseAnotherOption()
    {
        if (ImapOptions.Count < 2)
            return;

        var index = SelectedImapOption == null ? -1 : ImapOptions.IndexOf(SelectedImapOption);
        SelectedImapOption = ImapOptions[(index + 1) % ImapOptions.Count];
    }

    [RelayCommand]
    private async Task ShowFailureDetailsAsync()
    {
        var protocolLog = _failureProtocolLog;
        if (string.IsNullOrWhiteSpace(protocolLog))
            return;

        await _dialogService.ShowImapValidationFailedDialogAsync(FailureMessage, protocolLog);
    }

    [RelayCommand]
    private void HideFailure()
    {
        IsFailureOpen = false;
        _failureProtocolLog = null;
        _retryAction = AccountSetupRetryAction.None;
        OnPropertyChanged(nameof(IsRetryVisible));
        OnPropertyChanged(nameof(HasFailureDetails));
    }

    private void ShowFailure(string title, string message, InfoBarMessageType severity, AccountSetupRetryAction retryAction, string? protocolLog = null)
    {
        FailureTitle = title;
        FailureMessage = message;
        FailureSeverity = severity;
        _retryAction = retryAction;
        _failureProtocolLog = protocolLog;
        IsFailureOpen = true;

        OnPropertyChanged(nameof(IsRetryVisible));
        OnPropertyChanged(nameof(HasFailureDetails));
    }

    private void ShowOAuthFailure(OAuthSignInFailureKind kind, string? detail)
    {
        var (severity, message) = kind switch
        {
            OAuthSignInFailureKind.Cancelled => (InfoBarMessageType.Warning, Translator.ImapSetup_OAuthCancelled),
            OAuthSignInFailureKind.Denied => (InfoBarMessageType.Error, string.Format(Translator.ImapSetup_OAuthDenied, IssuerHost)),
            OAuthSignInFailureKind.AnotherSignInInProgress => (InfoBarMessageType.Warning, Translator.ImapSetup_OAuthAnotherSignInInProgress),
            OAuthSignInFailureKind.TimedOut => (InfoBarMessageType.Warning, Translator.ImapSetup_OAuthTimedOut),
            _ => (InfoBarMessageType.Error, string.IsNullOrWhiteSpace(detail)
                ? Translator.ImapSetup_OAuthFailedGeneric
                : string.Format(Translator.ImapSetup_OAuthFailed, detail))
        };

        ShowFailure(Translator.AccountSetup_Failure_SignInTitle, message, severity, AccountSetupRetryAction.BrowserSignIn);
    }

    /// <summary>Builds the options once per route; going back to the modes and forward again keeps the chosen one.</summary>
    private void BuildImapOptions()
    {
        IsImapOptionChosen = false;

        if (ImapOptions.Count > 0)
            return;

        var imap = _resolution?.Imap;
        var allowBrowser = IsSecureSignInSupportedByHost && _oauthTokenService != null;

        var options = (imap?.Options ?? Array.Empty<ImapSignInOption>())
            .Where(option => option.Kind != ImapSignInOptionKind.Browser || (allowBrowser && option.OAuthDiscovery?.CanSignIn == true))
            .Select(CreateOptionItem)
            .ToList();

        if (options.Count == 0)
            options.Add(CreateOptionItem(new ImapSignInOption { Kind = ImapSignInOptionKind.AccountPassword }));

        ImapOptions = options;
        OnPropertyChanged(nameof(ImapOptions));
        SelectedImapOption = options[0];
        NotifySignInLayoutChanged();
    }

    private AccountSetupImapOption CreateOptionItem(ImapSignInOption option)
    {
        var providerName = ProviderLabel;

        return option.Kind switch
        {
            ImapSignInOptionKind.Browser => new AccountSetupImapOption(
                option,
                Translator.AccountSetup_Option_Browser,
                option.OAuthDiscovery?.Profile?.TrustSource == OAuthTrustSource.BuiltIn && !string.IsNullOrWhiteSpace(providerName)
                    ? string.Format(Translator.AccountSetup_Option_BrowserDescription, providerName)
                    : Translator.AccountSetup_Option_BrowserDescriptionOrganization),
            ImapSignInOptionKind.AppPassword when option.PasswordKind == KnownImapPasswordKind.BridgePassword => new AccountSetupImapOption(
                option,
                Translator.ProviderSelection_PasswordHeader_Bridge,
                Translator.AccountSetup_Option_BridgePasswordDescription),
            ImapSignInOptionKind.AppPassword => new AccountSetupImapOption(
                option,
                Translator.AccountSetup_Option_AppPassword,
                Translator.AccountSetup_Option_AppPasswordDescription),
            _ => new AccountSetupImapOption(
                option,
                Translator.AccountSetup_Option_AccountPassword,
                Translator.AccountSetup_Option_AccountPasswordDescription)
        };
    }

    private string GetSetupHintText()
    {
        var option = SelectedImapOption?.Option;
        if (option == null || option.Kind == ImapSignInOptionKind.Browser)
            return string.Empty;

        var provider = ProviderLabel;

        return option.SetupHint switch
        {
            KnownImapSetupHint.AppPasswordRequired => string.Format(Translator.ProviderSelection_SetupHint_AppPasswordRequired, provider),
            KnownImapSetupHint.ImapDisabledByDefault => string.Format(Translator.ProviderSelection_SetupHint_ImapDisabledByDefault, provider),
            KnownImapSetupHint.LocalBridgeRequired => string.Format(Translator.ProviderSelection_SetupHint_LocalBridgeRequired_Device, provider),
            KnownImapSetupHint.AuthorizationCodeRequired => string.Format(Translator.ProviderSelection_SetupHint_AuthorizationCodeRequired, provider),
            KnownImapSetupHint.ThirdPartyAccessRequired => string.Format(Translator.ProviderSelection_SetupHint_ThirdPartyAccessRequired, provider),
            KnownImapSetupHint.SeparateEmailPasswordRequired => string.Format(Translator.ProviderSelection_SetupHint_SeparateEmailPasswordRequired, provider),
            KnownImapSetupHint.TwoFactorCodeSuffix => Translator.ProviderSelection_SetupHint_TwoFactorCodeSuffix,
            _ => option.Kind == ImapSignInOptionKind.AccountPassword ? Translator.ImapSetup_AppPasswordGenericHint : string.Empty
        };
    }

    private void BuildChecks()
    {
        Checks.Clear();

        if (!Capabilities.RequiresRemoteService)
            return;

        if (IsNativeRoute)
        {
            Checks.Add(new AccountSetupConnectionCheck(
                AccountSetupCheckKind.ProviderAccount,
                string.Format(Translator.AccountSetup_Check_ProviderAccount, NativeProviderName)));
        }
        else if (IsExchangeRoute)
        {
            Checks.Add(new AccountSetupConnectionCheck(
                AccountSetupCheckKind.ProviderAccount,
                string.Format(Translator.AccountSetup_Check_ProviderAccount, ExchangeProviderName)));
        }
        else
        {
            if (Capabilities.IsMailEnabled)
            {
                Checks.Add(new AccountSetupConnectionCheck(AccountSetupCheckKind.Incoming, Translator.AccountSetup_Check_Incoming));
                Checks.Add(new AccountSetupConnectionCheck(AccountSetupCheckKind.Outgoing, Translator.AccountSetup_Check_Outgoing));
            }

            if (IsCalendarRemote)
                Checks.Add(new AccountSetupConnectionCheck(AccountSetupCheckKind.Calendar, Translator.AccountSetup_Check_Calendar));

            if (IsContactRemote)
                Checks.Add(new AccountSetupConnectionCheck(AccountSetupCheckKind.Contacts, Translator.AccountSetup_Check_Contacts));
        }
    }

    private async Task RunImapChecksAsync(bool useOAuth)
    {
        var operation = StartOperation();
        HideFailure();
        ResetChecks();
        _pendingServer = null;
        SetSignInValidated(false);

        Phase = AccountSetupPhase.Checking;

        CustomServerInformation server;
        try
        {
            server = BuildImapServerInformation(useOAuth);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Building the server settings during account setup failed.");
            ShowFailure(Translator.AccountSetup_Failure_Title, ex.Message, InfoBarMessageType.Error, AccountSetupRetryAction.None);
            Phase = AccountSetupPhase.SignIn;
            return;
        }

        if (Capabilities.IsMailEnabled)
        {
            var mailChecks = Checks.Where(check => check.Kind is AccountSetupCheckKind.Incoming or AccountSetupCheckKind.Outgoing).ToArray();
            foreach (var check in mailChecks)
                check.State = ConnectionTestState.Testing;

            try
            {
                await ValidateImapConnectivityAsync(server, useOAuth, operation.Token);
            }
            catch (Exception ex)
            {
                if (operation.IsCancellationRequested)
                    return;

                var message = DescribeMailFailure(ex, server);
                foreach (var check in mailChecks)
                    FailCheck(check, message);

                ShowFailure(
                    Translator.AccountSetup_Failure_Title,
                    message,
                    InfoBarMessageType.Error,
                    useOAuth ? AccountSetupRetryAction.BrowserSignIn : AccountSetupRetryAction.PasswordCheck,
                    (ex as ImapValidationException)?.ProtocolLog);
                Phase = AccountSetupPhase.SignIn;
                return;
            }

            if (operation.IsCancellationRequested)
                return;

            foreach (var check in mailChecks)
                check.State = ConnectionTestState.Succeeded;
        }

        _pendingServer = server;
        await RunDavChecksAsync(server, operation);
    }

    /// <summary>
    /// Checks calendar and contacts with the token where <see cref="DavOAuthRule"/> allows it, otherwise with a password.
    /// A service the token does not cover and no password exists for waits on the DAV password row instead of failing.
    /// </summary>
    private async Task RunDavChecksAsync(CustomServerInformation server, CancellationTokenSource operation)
    {
        var address = EmailAddress?.Trim() ?? string.Empty;
        var needsPassword = false;
        var usesTokenEverywhere = true;
        var (davUser, davPassword) = GetDavPasswordCredential(server);

        foreach (var check in Checks.Where(check => check.Kind is AccountSetupCheckKind.Calendar or AccountSetupCheckKind.Contacts).ToArray())
        {
            var kind = check.Kind == AccountSetupCheckKind.Calendar ? DavServiceKind.CalDav : DavServiceKind.CardDav;
            var uri = ParseDavUri(kind == DavServiceKind.CalDav ? server.CalDavServiceUrl : server.CardDavServiceUrl);

            if (uri == null)
            {
                FailCheck(check, kind == DavServiceKind.CalDav ? Translator.ImapCalDavSettingsPage_CalDavUrlRequired : Translator.ImapSetup_CardDavNotFound);
                ShowFailure(Translator.AccountSetup_Failure_Title, check.Message, InfoBarMessageType.Error, AccountSetupRetryAction.None);
                Phase = AccountSetupPhase.SignIn;
                return;
            }

            DavAuthenticationProfile? bearer = null;
            if (_oauthSession != null && _davOAuthAuthenticationProvider != null && UsesDavToken(kind, uri, server))
            {
                try
                {
                    bearer = await _davOAuthAuthenticationProvider.TryCreateAsync(_oauthSession.Key, server, _oauthSession.Profile, kind, uri, operation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Information("The calendar or contacts token could not be prepared ({ErrorType}).", ex.GetType().Name);
                }
            }

            if (bearer == null)
            {
                usesTokenEverywhere = false;

                if (string.IsNullOrEmpty(davPassword))
                {
                    ResetCheck(check);
                    check.NeedsPassword = true;
                    needsPassword = true;
                    continue;
                }
            }

            check.NeedsPassword = false;
            check.State = ConnectionTestState.Testing;

            try
            {
                if (kind == DavServiceKind.CalDav)
                {
                    await _calDavClient.DiscoverCalendarsAsync(new CalDavConnectionSettings
                    {
                        ServiceUri = uri,
                        Username = davUser,
                        Password = davPassword,
                        Authentication = bearer
                    }, operation.Token);
                }
                else if (_cardDavClient != null)
                {
                    await _cardDavClient.DiscoverAsync(new CardDavConnectionSettings
                    {
                        ServiceUri = uri,
                        AccountAddress = address,
                        Authentication = bearer ?? new DavAuthenticationProfile
                        {
                            Kind = DavAuthenticationKind.Basic,
                            Username = davUser,
                            Password = davPassword
                        }
                    }, operation.Token);
                }
            }
            catch (Exception ex)
            {
                if (operation.IsCancellationRequested)
                    return;

                var message = string.Format(Translator.AccountSetup_Failure_Row, check.Title, string.Format(Translator.AccountSetup_Failure_Dav, uri.Host, ex.Message));
                FailCheck(check, message);
                ShowFailure(Translator.AccountSetup_Failure_Title, message, InfoBarMessageType.Error,
                    IsDavPasswordRequired ? AccountSetupRetryAction.DavCheck : _oauthSession != null ? AccountSetupRetryAction.BrowserSignIn : AccountSetupRetryAction.PasswordCheck);
                Phase = AccountSetupPhase.SignIn;
                return;
            }

            if (operation.IsCancellationRequested)
                return;

            check.State = ConnectionTestState.Succeeded;
        }

        if (needsPassword)
        {
            if (string.IsNullOrWhiteSpace(DavUserName))
                DavUserName = address;

            IsDavPasswordRequired = true;
            Phase = AccountSetupPhase.SignIn;
            return;
        }

        var usesDav = server.CalendarSupportMode == ImapCalendarSupportMode.CalDav || !string.IsNullOrWhiteSpace(server.CardDavServiceUrl);
        server.CalDavUsername = usesDav ? davUser : string.Empty;
        server.CalDavPassword = usesDav && !usesTokenEverywhere ? davPassword : string.Empty;

        _pendingServer = server;
        SetSignInValidated(true);
        Phase = AccountSetupPhase.SignIn;
    }

    private (string User, string Password) GetDavPasswordCredential(CustomServerInformation server)
    {
        var hasManualEntry = IsCalDavManualEntryVisible || IsCardDavManualEntryVisible;
        var usesSeparatePassword = _oauthSession != null ||
                                   IsDavPasswordRequired ||
                                   (hasManualEntry && Capabilities.IsMailEnabled && !UseMailSignInForDav);

        if (usesSeparatePassword)
        {
            var user = string.IsNullOrWhiteSpace(DavUserName) ? EmailAddress?.Trim() ?? string.Empty : DavUserName.Trim();
            return (user, DavPassword ?? string.Empty);
        }

        var mailUser = string.IsNullOrWhiteSpace(server.IncomingServerUsername) ? EmailAddress?.Trim() ?? string.Empty : server.IncomingServerUsername;
        if (!string.IsNullOrWhiteSpace(server.CalDavUsername))
            mailUser = server.CalDavUsername;

        return (mailUser, Password ?? string.Empty);
    }

    private bool UsesDavToken(DavServiceKind kind, Uri uri, CustomServerInformation server)
    {
        var session = _oauthSession;
        return session != null &&
               DavOAuthRule.UsesToken(
                   ImapAuthenticationMethod.OAuth2,
                   _grantedScopes,
                   kind,
                   uri,
                   server.IncomingServer,
                   session.Profile.Issuer,
                   _providerCatalog.GetOAuthDavSettings(session.Profile));
    }

    private static async Task<IReadOnlyCollection<string>> ReadGrantedScopesAsync(IOAuthTokenService tokenService, Guid key)
    {
        try
        {
            return await tokenService.GetGrantedScopesAsync(key);
        }
        catch (Exception ex)
        {
            Log.Warning("The granted OAuth scopes could not be read ({ErrorType}).", ex.GetType().Name);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The servers the checks use: a catalog provider's from <see cref="ISpecialImapProviderConfigResolver"/> with the
    /// chosen region and login address, otherwise the discovered ones with the typed credential.
    /// </summary>
    private CustomServerInformation BuildImapServerInformation(bool useOAuth)
    {
        var imap = _resolution?.Imap ?? throw new InvalidOperationException(Translator.Exception_ImapAutoDiscoveryFailed);
        var address = EmailAddress?.Trim() ?? string.Empty;
        var password = useOAuth ? string.Empty : Password ?? string.Empty;
        var calendarMode = CalendarSupportMode;
        var provider = imap.Provider;
        CustomServerInformation server;

        if (provider != null && provider.SpecialImapProvider != SpecialImapProvider.None)
        {
            var loginAddress = SelectedImapOption?.Option.RequiresLoginAddress == true ? LoginAddress?.Trim() : null;
            var details = new SpecialImapProviderDetails(
                address,
                password,
                EffectiveSenderName,
                provider.SpecialImapProvider,
                calendarMode,
                SelectedRegion?.Id ?? imap.DefaultRegion?.Id ?? string.Empty,
                loginAddress ?? string.Empty);
            var creation = new AccountCreationDialogResult(
                MailProviderType.IMAP4,
                AccountName?.Trim() ?? string.Empty,
                details,
                SelectedColor?.Hex ?? string.Empty,
                SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths,
                Capabilities.IsMailEnabled,
                Capabilities.IsCalendarEnabled,
                Capabilities.IsContactEnabled,
                Capabilities.IsTaskEnabled,
                calendarMode);

            server = _specialImapProviderConfigResolver.GetServerInformation(new MailAccount
            {
                ProviderType = MailProviderType.IMAP4,
                SpecialImapProvider = provider.SpecialImapProvider,
                IsMailAccessGranted = Capabilities.IsMailEnabled,
                IsCalendarAccessGranted = calendarMode != ImapCalendarSupportMode.Disabled,
                IsContactAccessGranted = IsContactRemote,
                ContactIntegrationSource = IsContactRemote ? AccountIntegrationSource.Dav : AccountIntegrationSource.Local
            }, creation);
        }
        else if (imap.ServerInformation != null)
        {
            server = imap.ServerInformation.CloneForCredentialStorage();
            server.PendingCertificateTrusts = [];
            server.ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected;

            if (string.IsNullOrWhiteSpace(server.IncomingServerUsername))
                server.IncomingServerUsername = address;

            if (string.IsNullOrWhiteSpace(server.OutgoingServerUsername))
                server.OutgoingServerUsername = server.IncomingServerUsername;

            server.IncomingServerPassword = password;
            server.OutgoingServerPassword = password;
        }
        else
        {
            throw new InvalidOperationException(Translator.Exception_ImapAutoDiscoveryFailed);
        }

        server.Id = Guid.NewGuid();
        server.AccountId = Guid.Empty;
        server.Address = address;
        server.DisplayName = EffectiveSenderName;
        server.IncomingServerType = CustomIncomingServerType.IMAP4;
        server.CalendarSupportMode = calendarMode;

        // A typed address wins; a catalog provider's own URL already follows the chosen region.
        var calDavUri = IsCalDavManualEntryVisible || string.IsNullOrWhiteSpace(server.CalDavServiceUrl) ? CalDavServiceUri : ParseDavUri(server.CalDavServiceUrl);
        var cardDavUri = IsCardDavManualEntryVisible || string.IsNullOrWhiteSpace(server.CardDavServiceUrl) ? CardDavServiceUri : ParseDavUri(server.CardDavServiceUrl);
        server.CalDavServiceUrl = IsCalendarRemote ? calDavUri?.ToString() ?? string.Empty : string.Empty;
        server.CardDavServiceUrl = IsContactRemote ? cardDavUri?.ToString() ?? string.Empty : string.Empty;

        if (useOAuth)
        {
            server.IncomingAuthenticationMethod = ImapAuthenticationMethod.OAuth2;
            server.OutgoingAuthenticationMethod = ImapAuthenticationMethod.OAuth2;
            server.IncomingServerPassword = string.Empty;
            server.OutgoingServerPassword = string.Empty;
        }

        return server;
    }

    private async Task ValidateImapConnectivityAsync(CustomServerInformation server, bool useOAuth, CancellationToken cancellationToken)
    {
        while (true)
        {
            var session = _oauthSession;
            var tokenService = _oauthTokenService;

            var result = useOAuth && session != null && tokenService != null
                ? await _synchronizationManager.TestImapConnectivityAsync(server, tokenService, session.Key)
                : await _synchronizationManager.TestImapConnectivityAsync(server);

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.IsCertificateUIRequired)
            {
                if (!result.IsSuccess)
                    throw new ImapValidationException(result.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage, result.ProtocolLog);

                return;
            }

            await ServerCertificateTrustFlow.AcceptAsync(
                result.CertificateFailure,
                result.FailedReason,
                server,
                server.AccountId,
                _dialogService.ShowServerCertificateTrustDialogAsync);
        }
    }

    /// <summary>MailKit reports a rejected login only as text, so an authentication wording maps to the password message.</summary>
    private string DescribeMailFailure(Exception exception, CustomServerInformation server)
    {
        var reason = exception.Message ?? string.Empty;
        var isAuthenticationFailure = exception is ImapValidationException &&
                                      SelectedImapOption?.IsPassword == true &&
                                      (reason.Contains("authenticat", StringComparison.OrdinalIgnoreCase) ||
                                       reason.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                                       reason.Contains("password", StringComparison.OrdinalIgnoreCase));

        var message = isAuthenticationFailure
            ? string.Format(Translator.AccountSetup_Failure_PasswordRejected, ProviderLabel)
            : string.Format(Translator.AccountSetup_Failure_Connection, server.IncomingServer, reason);

        return string.Format(Translator.AccountSetup_Failure_Row, Translator.ProviderSelection_UseForMail, message);
    }

    private void ResetChecks()
    {
        foreach (var check in Checks)
            ResetCheck(check);
    }

    private static void ResetCheck(AccountSetupConnectionCheck? check)
    {
        if (check == null)
            return;

        check.State = ConnectionTestState.NotTested;
        check.NeedsPassword = false;
        check.Message = string.Empty;
    }

    private static void FailCheck(AccountSetupConnectionCheck? check, string message)
    {
        if (check == null)
            return;

        check.NeedsPassword = false;
        check.State = ConnectionTestState.Failed;
        check.Message = message;
    }

    private void DiscardOAuthSession()
    {
        var session = _oauthSession;
        _oauthSession = null;
        _grantedScopes = Array.Empty<string>();

        if (session != null)
            _oauthTokenService?.DiscardSession(session.Key);
    }

    /// <summary>
    /// Forgets every credential of the current attempt: the native sign-in (and its Gmail token file), the OAuth session
    /// and the typed passwords. A password must never travel to a different address or feature set.
    /// </summary>
    private async Task DiscardSignInAsync()
    {
        await DiscardNativeSignInAsync();
        await DiscardExchangeSignInAsync();
        DiscardOAuthSession();

        _pendingServer = null;
        SetSignInValidated(false);
        Password = string.Empty;
        DavPassword = string.Empty;
        IsDavPasswordRequired = false;
        IsIssuerConfirmed = false;
        OAuthState = AccountSetupOAuthState.Idle;
        HideFailure();
        ResetChecks();
    }

    private void ResetImapSignIn()
    {
        ImapOptions = [];
        OnPropertyChanged(nameof(ImapOptions));
        SelectedImapOption = null;
        Regions = [];
        OnPropertyChanged(nameof(Regions));
        SelectedRegion = null;
        LoginAddress = string.Empty;
        DavUserName = string.Empty;
        Checks.Clear();
        NotifySignInLayoutChanged();
    }

    /// <summary>Editing a credential after a check returns the option to unchecked; Add account waits for a new check.</summary>
    private void InvalidateCheckedCredential()
    {
        if (!(IsSignInValidated || Phase == AccountSetupPhase.Checking) || IsNativeRoute)
            return;

        CancelOperation();
        _pendingServer = null;
        ResetChecks();
        SetSignInValidated(false);
        Phase = AccountSetupPhase.SignIn;
    }

    private void SetSignInValidated(bool value)
    {
        if (_isSignInValidated == value)
            return;

        _isSignInValidated = value;
        OnPropertyChanged(nameof(IsSignInValidated));
        NotifySignInLayoutChanged();
        BuildKnownItems();
        AddAccountCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedImapOptionChanged(AccountSetupImapOption? oldValue, AccountSetupImapOption? newValue)
    {
        if (oldValue?.IsBrowser == true && newValue?.IsBrowser != true)
        {
            DiscardOAuthSession();
            OAuthState = AccountSetupOAuthState.Idle;
        }

        Password = string.Empty;
        IsIssuerConfirmed = false;
        IsDavPasswordRequired = false;
        HideFailure();
        InvalidateCheckedCredential();

        var imap = _resolution?.Imap;
        Regions = newValue?.Option.ShowsRegions == true && imap != null ? imap.Regions.ToList() : [];
        OnPropertyChanged(nameof(Regions));
        SelectedRegion = Regions.Count == 0 ? null : imap?.DefaultRegion is { } region && Regions.Contains(region) ? region : Regions[0];

        NotifySignInLayoutChanged();
        CheckPasswordCommand.NotifyCanExecuteChanged();
        SignInWithBrowserCommand.NotifyCanExecuteChanged();
    }

    partial void OnPasswordChanged(string value) => InvalidateCheckedCredential();

    partial void OnLoginAddressChanged(string value) => InvalidateCheckedCredential();

    partial void OnSelectedRegionChanged(KnownImapProviderRegion? value) => InvalidateCheckedCredential();

    partial void OnDavPasswordChanged(string value)
    {
        if (IsDavPasswordRequired)
            InvalidateCheckedCredential();
    }

    partial void OnIsDavPasswordRequiredChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDavPasswordVisible));
        NotifyStepContentChanged();
    }

    private void NotifySignInLayoutChanged()
    {
        OnPropertyChanged(nameof(IsSignInBlockVisible));
        OnPropertyChanged(nameof(IsNativeSignInVisible));
        OnPropertyChanged(nameof(IsNativeSignInButtonVisible));
        OnPropertyChanged(nameof(IsFooterSocialSignInVisible));
        OnPropertyChanged(nameof(IsOAuthWaiting));
        OnPropertyChanged(nameof(SignedInTitle));
        OnPropertyChanged(nameof(IsImapSignInVisible));
        OnPropertyChanged(nameof(SelectedImapOptionDescription));
        OnPropertyChanged(nameof(SignInCaption));
        OnPropertyChanged(nameof(IsSignInCaptionVisible));
        OnPropertyChanged(nameof(IsSetupHintCaptionVisible));
        OnPropertyChanged(nameof(IsBrowserPanelVisible));
        OnPropertyChanged(nameof(IsPasswordPanelVisible));
        OnPropertyChanged(nameof(BrowserSignInButtonText));
        OnPropertyChanged(nameof(IsBrowserSignInButtonVisible));
        OnPropertyChanged(nameof(IsIssuerConfirmationVisible));
        OnPropertyChanged(nameof(IssuerConfirmationText));
        OnPropertyChanged(nameof(PasswordHeader));
        OnPropertyChanged(nameof(SetupHintText));
        OnPropertyChanged(nameof(HelpUrl));
        OnPropertyChanged(nameof(IsHelpLinkVisible));
        OnPropertyChanged(nameof(HelpLinkText));
        OnPropertyChanged(nameof(IsRegionVisible));
        OnPropertyChanged(nameof(RegionDescription));
        OnPropertyChanged(nameof(IsLoginAddressVisible));
        OnPropertyChanged(nameof(LoginAddressHeader));
        OnPropertyChanged(nameof(LoginAddressDescription));
        OnPropertyChanged(nameof(IsDavPasswordVisible));
        OnPropertyChanged(nameof(IsUseAnotherOptionVisible));
        NotifyExchangeSignInLayoutChanged();
        NotifyConnectionStatusChanged();
        NotifyStepContentChanged();
    }

    private void OnChecksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var check in _observedChecks)
            check.PropertyChanged -= OnCheckPropertyChanged;

        _observedChecks.Clear();

        foreach (var check in Checks)
        {
            check.PropertyChanged += OnCheckPropertyChanged;
            _observedChecks.Add(check);
        }

        NotifyConnectionStatusChanged();
    }

    private void OnCheckPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountSetupConnectionCheck.State) or
            nameof(AccountSetupConnectionCheck.NeedsPassword) or
            nameof(AccountSetupConnectionCheck.Message))
        {
            NotifyConnectionStatusChanged();
        }
    }

    private void NotifyConnectionStatusChanged()
    {
        OnPropertyChanged(nameof(ConnectionStatusKind));
        OnPropertyChanged(nameof(IsConnectionStatusVisible));
        OnPropertyChanged(nameof(IsConnectionStatusBusy));
        OnPropertyChanged(nameof(IsConnectionStatusConnected));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }
}
