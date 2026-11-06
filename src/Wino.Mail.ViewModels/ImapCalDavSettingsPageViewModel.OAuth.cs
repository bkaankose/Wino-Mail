#nullable enable
using System;
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
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Secure sign-in (OAuth 2.0) for IMAP and SMTP. The sign-in result lives only in this ViewModel
/// until the account is saved; cancelling or leaving setup discards it.
/// </summary>
public partial class ImapCalDavSettingsPageViewModel
{
    // Every new IMAP setup runs these checks, so each one is short: a silent server must never
    // hold up the password path noticeably.
    private static readonly TimeSpan SecureSignInCheckTimeout = TimeSpan.FromSeconds(5);

    private const string LoopbackRedirectUri = "http://127.0.0.1";

    private readonly IOAuthTokenService? _oauthTokenService;
    private readonly IOAuthIssuerDiscoveryService? _oauthIssuerDiscoveryService;
    private readonly IImapAuthCapabilityProbe? _imapAuthCapabilityProbe;
    private readonly IAccountOAuthProfileService? _accountOAuthProfileService;

    private OAuthDiscoveryResult _oauthDiscovery = OAuthDiscoveryResult.NotOffered;
    private ImapAuthCapabilities _authCapabilities = ImapAuthCapabilities.Unknown;
    private OAuthSetupSession? _oauthSession;
    private bool _isOAuthSessionHandedOff;
    private CancellationTokenSource? _oauthSignInCancellation;
    private AccountOAuthProfile? _storedOAuthProfile;
    private ImapAuthenticationMethod _discoveredIncomingMethod = ImapAuthenticationMethod.Auto;
    private ImapAuthenticationMethod _discoveredOutgoingMethod = ImapAuthenticationMethod.Auto;

    /// <summary>
    /// Set by a head that renders the secure sign-in choice after the address step. Heads that
    /// do not set it keep the password-only flow and never run the secure sign-in checks.
    /// </summary>
    public bool IsSecureSignInSupportedByHost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    public partial ImapSignInStage SignInStage { get; set; }

    /// <summary>
    /// Optional IMAP and SMTP host typed on the address step. It replaces the discovered hosts.
    /// </summary>
    [ObservableProperty]
    public partial string MailServerAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UsePasswordInsteadOfOAuth { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithOAuthCommand))]
    public partial bool IsIssuerConfirmed { get; set; }

    [ObservableProperty]
    public partial bool IsOrganizationSignInExpanded { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithOrganizationClientCommand))]
    public partial string CustomOAuthIssuer { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithOrganizationClientCommand))]
    public partial string CustomOAuthClientId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CustomOAuthScopes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOAuthSigningIn { get; set; }

    [ObservableProperty]
    public partial bool IsOAuthInfoBarOpen { get; set; }

    [ObservableProperty]
    public partial string OAuthInfoBarMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarMessageType OAuthInfoBarType { get; set; }

    [ObservableProperty]
    public partial bool IsAdvancedExpanded { get; set; }

    public bool IsSignInIdentityStageVisible => IsSignInStepVisible && SignInStage == ImapSignInStage.Identity;
    public bool IsSignInMethodStageVisible => IsSignInStepVisible && SignInStage == ImapSignInStage.Method;
    public bool IsSignInMailServerAddressVisible => IsMailSupportEnabled;

    public bool IsOAuthSignedIn => _oauthSession != null;

    public bool IsOAuthChoiceVisible => IsSignInMethodStageVisible && IsOAuthMethodChosen && !IsOAuthSignedIn;
    public bool IsOAuthSignedInVisible => IsSignInMethodStageVisible && IsOAuthMethodChosen && IsOAuthSignedIn;
    public bool IsPasswordMethodVisible => IsSignInMethodStageVisible && !IsOAuthMethodChosen;
    public bool IsUseSecureSignInVisible => IsPasswordMethodVisible && (IsOAuthSignInOffered || IsOAuthSignedIn);
    public bool IsOrganizationSignInOptionVisible => IsPasswordMethodVisible && _oauthDiscovery.OfferedWithoutClient && !IsOAuthSignInOffered;
    public bool IsOrganizationSignInFieldsVisible => IsOrganizationSignInOptionVisible && IsOrganizationSignInExpanded;
    public bool IsIssuerConfirmationVisible => IsOAuthChoiceVisible && _oauthDiscovery.RequiresIssuerConfirmation;
    public bool IsNoRefreshTokenWarningVisible => IsOAuthSignedInVisible && _oauthSession is { HasRefreshToken: false };

    /// <summary>
    /// Until the user signs in on the secure sign-in card there is nothing to continue with.
    /// </summary>
    public bool IsPrimaryActionVisible => !IsOAuthChoiceVisible;

    public string SignInAddressSummary => EmailAddress?.Trim() ?? string.Empty;
    public string SecureSignInTitle => string.Format(Translator.ImapSetup_SecureSignInTitle, AddressDomain);

    public string OAuthSignInButtonText
    {
        get
        {
            var providerName = _oauthDiscovery.Profile?.TrustSource == OAuthTrustSource.BuiltIn
                ? _knownImapProviderCatalog?.Match(EmailAddress?.Trim() ?? string.Empty, IncomingServer)?.DisplayName
                : null;

            return string.IsNullOrWhiteSpace(providerName)
                ? Translator.ImapSetup_SignInWithOrganization
                : string.Format(Translator.ImapSetup_SignInWithProvider, providerName);
        }
    }

    public string OAuthIssuerHost => _oauthSession != null
        ? GetHost(_oauthSession.Profile.Issuer)
        : !string.IsNullOrWhiteSpace(_oauthDiscovery.IssuerHost)
            ? _oauthDiscovery.IssuerHost
            : GetHost(_oauthDiscovery.Profile?.Issuer);

    public string IssuerConfirmationText => string.Format(Translator.ImapSetup_OAuthConfirmIssuer, OAuthIssuerHost);
    public string OAuthSignedInTitle => string.Format(Translator.ImapSetup_OAuthSignedInAs, SignInAddressSummary);
    public string OAuthSignedInDescription => string.Format(Translator.ImapSetup_OAuthSignedInThrough, OAuthIssuerHost);
    public string OAuthCustomSchemeRedirectUri => OAuthRedirect.CustomSchemeRedirectUri.ToString();
    public string OAuthLoopbackRedirectUri => LoopbackRedirectUri;

    public bool IsIncomingOAuthSelected => GetAuthenticationMethodByIndex(SelectedIncomingServerAuthenticationMethodIndex) == ImapAuthenticationMethod.OAuth2;
    public bool IsOutgoingOAuthSelected => GetAuthenticationMethodByIndex(SelectedOutgoingServerAuthenticationMethodIndex) == ImapAuthenticationMethod.OAuth2;
    public bool IsOAuthServerSignInVisible => IsIncomingOAuthSelected || IsOutgoingOAuthSelected;
    public bool IsIncomingPasswordSignInVisible => !IsIncomingOAuthSelected;
    public bool IsOutgoingSharedSignInOptionVisible => !IsOutgoingOAuthSelected;

    public string OAuthServerSignInText => IsOAuthSignedIn
        ? string.Format(Translator.ImapSetup_OAuthSignedInAsThrough, SignInAddressSummary, OAuthIssuerHost)
        : _storedOAuthProfile != null
            ? string.Format(Translator.ImapSetup_OAuthSignsInThrough, GetHost(_storedOAuthProfile.Issuer))
            : Translator.ImapSetup_OAuthSignInRequired;

    public bool IsEditOAuthSignInVisible => IsEditSignInVisible && IsIncomingOAuthSelected;
    public bool IsEditPasswordSignInVisible => IsEditSignInVisible && !IsIncomingOAuthSelected;

    public string EditOAuthDescription => _storedOAuthProfile == null
        ? Translator.ImapSetup_OAuthProfileMissing
        : IsStoredOAuthSignInSupported
            ? string.Format(Translator.ImapSetup_OAuthSignsInThrough, GetHost(_storedOAuthProfile.Issuer))
            : string.Format(Translator.ImapSetup_OAuthSignInUnavailableOnDevice, GetHost(_storedOAuthProfile.Issuer));

    /// <summary>
    /// A stored sign-in that needs a loopback redirect cannot run again on a device without one (iOS); the account
    /// then keeps working only after its servers switch to a password.
    /// </summary>
    private bool IsStoredOAuthSignInSupported => _storedOAuthProfile == null
                                                 || _oauthTokenService?.IsRedirectSupported(_storedOAuthProfile.RedirectKind) != false;

    private bool IsOAuthSignInOffered => _oauthDiscovery.CanSignIn;

    // Secure sign-in is the chosen method once the user signed in, or while the server offers it
    // and the user has not switched to a password.
    private bool IsOAuthMethodChosen => !UsePasswordInsteadOfOAuth && (IsOAuthSignedIn || IsOAuthSignInOffered);

    private bool CanOfferSecureSignIn => IsSecureSignInSupportedByHost
                                         && IsCreateMode
                                         && IsMailSupportEnabled
                                         && !IsPop3
                                         && _oauthTokenService != null
                                         && _oauthIssuerDiscoveryService != null
                                         && _imapAuthCapabilityProbe != null;

    private bool HasOAuthCredentialForTest => _oauthTokenService != null && (IsCreateMode ? _oauthSession != null : _storedOAuthProfile != null);

    private bool ShouldListOAuthMethod => !IsPop3 && (_authCapabilities.SupportsOAuth
                                         || _oauthDiscovery.CanSignIn
                                         || _oauthDiscovery.OfferedWithoutClient
                                         || _oauthSession != null
                                         || _storedOAuthProfile != null
                                         || IsAdvancedExpanded
                                         || IsIncomingOAuthSelected
                                         || IsOutgoingOAuthSelected);

    private bool CanStartDiscoveredOAuthSignIn => !IsBusy
                                                  && !IsOAuthSigningIn
                                                  && _oauthDiscovery.CanSignIn
                                                  && (!_oauthDiscovery.RequiresIssuerConfirmation || IsIssuerConfirmed);

    private bool CanSignInAgain => !IsBusy && !IsOAuthSigningIn && (_oauthSession != null || _oauthDiscovery.CanSignIn);

    private bool CanStartOrganizationSignIn => !IsBusy
                                               && !IsOAuthSigningIn
                                               && Uri.TryCreate(CustomOAuthIssuer?.Trim(), UriKind.Absolute, out var issuer)
                                               && issuer.Scheme == Uri.UriSchemeHttps
                                               && !string.IsNullOrWhiteSpace(CustomOAuthClientId);

    private bool CanReauthenticateOAuth => !IsBusy && !IsOAuthSigningIn && _storedOAuthProfile != null && _oauthTokenService != null && IsStoredOAuthSignInSupported;

    [RelayCommand(CanExecute = nameof(CanStartDiscoveredOAuthSignIn))]
    private Task SignInWithOAuthAsync() => RunOAuthSignInAsync(_oauthDiscovery.Profile);

    /// <summary>
    /// Signs in again with the registration that issued the current session, so a dynamically
    /// registered client is reused instead of registering another one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignInAgain))]
    private Task SignInAgainAsync() => RunOAuthSignInAsync(_oauthSession?.Profile ?? _oauthDiscovery.Profile);

    [RelayCommand(CanExecute = nameof(CanStartOrganizationSignIn))]
    private Task SignInWithOrganizationClientAsync() => RunOAuthSignInAsync(BuildUserEnteredProfile());

    [RelayCommand]
    private void UsePasswordInstead()
    {
        CancelOAuthSignIn();
        UsePasswordInsteadOfOAuth = true;
    }

    [RelayCommand]
    private void UseSecureSignInInstead() => UsePasswordInsteadOfOAuth = false;

    [RelayCommand]
    private void ToggleOrganizationSignIn() => IsOrganizationSignInExpanded = !IsOrganizationSignInExpanded;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void ChangeAddress() => Back();

    [RelayCommand]
    private void HideOAuthInfoBar() => IsOAuthInfoBarOpen = false;

    [RelayCommand(CanExecute = nameof(CanReauthenticateOAuth))]
    private async Task ReauthenticateOAuthAsync()
    {
        var profile = _storedOAuthProfile;
        var tokenService = _oauthTokenService;
        if (profile == null || tokenService == null)
            return;

        HideOAuthInfoBar();
        CancelOAuthSignIn();

        var cancellation = new CancellationTokenSource();
        _oauthSignInCancellation = cancellation;
        IsOAuthSigningIn = true;

        try
        {
            if (_accountOAuthProfileService != null)
                profile = await _accountOAuthProfileService.GetAsync(_editingAccountId) ?? profile;

            var supportedScopes = await GetEditSupportedScopesAsync(profile);
            var preparedProfile = PrepareProfileForSelectedModes(profile, supportedScopes, registeredScopes: null);

            // The returned profile can carry a client id registered during this sign-in.
            var usedProfile = await tokenService.ReauthenticateAsync(_editingAccountId, preparedProfile, GetLoginHint(), cancellation.Token);
            if (usedProfile != null)
            {
                usedProfile.AccountId = _editingAccountId;

                if (_accountOAuthProfileService != null)
                    await _accountOAuthProfileService.SaveAsync(usedProfile);

                _storedOAuthProfile = usedProfile;
            }

            await _accountService.ClearAccountAttentionAsync(_editingAccountId);

            await RefreshDavGrantedScopesAsync();
            await UpdateEditDavScopeRequirementAsync();
            NotifySecureSignInLayoutChanged();
            ResetConnectionStates();
            IsImapValidationSucceeded = false;
            ShowOAuthInfoBar(InfoBarMessageType.Success, Translator.ImapSetup_OAuthReauthenticated);
        }
        catch (OAuthSignInException ex)
        {
            ShowOAuthSignInFailure(ex.Kind, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            ShowOAuthSignInFailure(OAuthSignInFailureKind.Cancelled, null);
        }
        catch (OperationCanceledException)
        {
            // The page was left or the sign-in was replaced; nothing to report.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Signing in again to an OAuth IMAP account failed.");
            ShowOAuthSignInFailure(OAuthSignInFailureKind.Failed, ex.Message);
        }
        finally
        {
            CompleteOAuthSignInAttempt(cancellation);
        }
    }

    private async Task RunOAuthSignInAsync(AccountOAuthProfile? profile)
    {
        var tokenService = _oauthTokenService;
        if (profile == null || tokenService == null)
            return;

        HideOAuthInfoBar();
        CancelOAuthSignIn();

        var cancellation = new CancellationTokenSource();
        _oauthSignInCancellation = cancellation;
        IsOAuthSigningIn = true;
        var continueToServers = false;

        try
        {
            var preparedProfile = PrepareProfileForSelectedModes(profile, _oauthDiscovery.SupportedScopes, GetSetupRegisteredScopes(profile));
            var session = await tokenService.SignInAsync(preparedProfile, GetLoginHint(), cancellation.Token);

            if (cancellation.IsCancellationRequested)
            {
                tokenService.DiscardSession(session.Key);
                return;
            }

            // A failed "Sign in again" keeps the earlier session, so it is replaced only here.
            DiscardOAuthSession();
            _oauthSession = session;
            UsePasswordInsteadOfOAuth = false;
            ResetConnectionStates();
            IsImapValidationSucceeded = false;
            UpdateOAuthMethodListing();
            await RefreshDavGrantedScopesAsync();
            UpdateSetupDavScopeRequirement();
            NotifySecureSignInLayoutChanged();

            // Without a refresh token the sign-in step keeps showing its warning instead of moving on.
            continueToServers = IsCreateMode &&
                                CurrentSetupStep == ImapSetupStep.SignIn &&
                                SignInStage == ImapSignInStage.Method &&
                                session.HasRefreshToken &&
                                !IsDavScopeSignInRequired;
        }
        catch (OAuthSignInException ex)
        {
            ShowOAuthSignInFailure(ex.Kind, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            ShowOAuthSignInFailure(OAuthSignInFailureKind.Cancelled, null);
        }
        catch (OperationCanceledException)
        {
            // The page was left or setup was cancelled; nothing to report.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Secure sign-in failed during IMAP setup.");
            ShowOAuthSignInFailure(OAuthSignInFailureKind.Failed, ex.Message);
        }
        finally
        {
            CompleteOAuthSignInAttempt(cancellation);
        }

        if (continueToServers)
            await ContinueFromSignInMethodAsync();
    }

    private void CompleteOAuthSignInAttempt(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_oauthSignInCancellation, cancellation))
            _oauthSignInCancellation = null;

        cancellation.Dispose();
        IsOAuthSigningIn = false;
    }

    private void CancelOAuthSignIn()
    {
        var cancellation = _oauthSignInCancellation;
        _oauthSignInCancellation = null;

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt finished between reading the field and cancelling it.
        }
    }

    private void ShowOAuthSignInFailure(OAuthSignInFailureKind kind, string? detail)
    {
        var (type, message) = kind switch
        {
            OAuthSignInFailureKind.Cancelled => (InfoBarMessageType.Warning, Translator.ImapSetup_OAuthCancelled),
            OAuthSignInFailureKind.Denied => (InfoBarMessageType.Error, string.Format(Translator.ImapSetup_OAuthDenied, OAuthIssuerHostOrStored)),
            OAuthSignInFailureKind.AnotherSignInInProgress => (InfoBarMessageType.Warning, Translator.ImapSetup_OAuthAnotherSignInInProgress),
            OAuthSignInFailureKind.TimedOut => (InfoBarMessageType.Warning, Translator.ImapSetup_OAuthTimedOut),
            _ => (InfoBarMessageType.Error, string.IsNullOrWhiteSpace(detail)
                ? Translator.ImapSetup_OAuthFailedGeneric
                : string.Format(Translator.ImapSetup_OAuthFailed, detail))
        };

        ShowOAuthInfoBar(type, message);
    }

    private string OAuthIssuerHostOrStored => IsEditMode && _storedOAuthProfile != null
        ? GetHost(_storedOAuthProfile.Issuer)
        : OAuthIssuerHost;

    private void ShowOAuthInfoBar(InfoBarMessageType type, string message)
    {
        OAuthInfoBarType = type;
        OAuthInfoBarMessage = message;
        IsOAuthInfoBarOpen = true;
    }

    /// <summary>
    /// Reads the server's sign-in mechanisms, then looks for an issuer Wino can use. Any failure or
    /// timeout leaves the password path exactly as it was.
    /// </summary>
    private async Task DiscoverSecureSignInAsync()
    {
        if (!CanOfferSecureSignIn ||
            string.IsNullOrWhiteSpace(IncomingServer) ||
            !IsValidPort(IncomingServerPort))
        {
            UpdateOAuthMethodListing();
            return;
        }

        var serverInformation = BuildServerInformation();
        var address = EmailAddress.Trim();

        BusyText = Translator.ImapSetup_CheckingSignInMethods;
        _authCapabilities = await ProbeAuthCapabilitiesAsync(serverInformation);

        // IMAP can only use OAuth through a SASL mechanism the server advertises; without one there
        // is nothing to discover, which keeps password-only servers as fast as before.
        if (_authCapabilities.SupportsOAuth)
        {
            BusyText = Translator.ImapSetup_LookingForSignInPage;
            _oauthDiscovery = await DiscoverIssuerAsync(address, serverInformation.IncomingServer, _authCapabilities);
        }

        PrefillOrganizationSignIn();
        UpdateOAuthMethodListing();
        NotifySecureSignInLayoutChanged();
    }

    private async Task<ImapAuthCapabilities> ProbeAuthCapabilitiesAsync(CustomServerInformation serverInformation)
    {
        using var timeout = new CancellationTokenSource(SecureSignInCheckTimeout);

        try
        {
            var capabilities = await _imapAuthCapabilityProbe!
                .ProbeAsync(serverInformation, timeout.Token)
                .WaitAsync(SecureSignInCheckTimeout);

            return capabilities ?? ImapAuthCapabilities.Unknown;
        }
        catch (Exception ex)
        {
            Log.Information("Secure sign-in check skipped for IMAP setup: the capability probe ended with {Reason}.", ex.GetType().Name);
            return ImapAuthCapabilities.Unknown;
        }
    }

    private async Task<OAuthDiscoveryResult> DiscoverIssuerAsync(string address, string imapHost, ImapAuthCapabilities capabilities)
    {
        using var timeout = new CancellationTokenSource(SecureSignInCheckTimeout);

        try
        {
            var result = await _oauthIssuerDiscoveryService!
                .DiscoverAsync(address, imapHost, capabilities, timeout.Token)
                .WaitAsync(SecureSignInCheckTimeout);

            return result ?? OAuthDiscoveryResult.NotOffered;
        }
        catch (Exception ex)
        {
            Log.Information("Secure sign-in check skipped for IMAP setup: issuer discovery ended with {Reason}.", ex.GetType().Name);
            return OAuthDiscoveryResult.NotOffered;
        }
    }

    private void PrefillOrganizationSignIn()
    {
        if (!_oauthDiscovery.OfferedWithoutClient || !string.IsNullOrWhiteSpace(CustomOAuthIssuer))
            return;

        if (!string.IsNullOrWhiteSpace(_oauthDiscovery.IssuerHost))
            CustomOAuthIssuer = $"https://{_oauthDiscovery.IssuerHost}";
    }

    private AccountOAuthProfile? BuildUserEnteredProfile()
    {
        if (!CanStartOrganizationSignIn)
            return null;

        var scopes = (CustomOAuthScopes ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AccountOAuthProfile
        {
            Issuer = CustomOAuthIssuer.Trim(),
            ClientId = CustomOAuthClientId.Trim(),
            Scopes = string.Join(' ', scopes),
            RedirectKind = OAuthRedirectKind.CustomScheme,
            TrustSource = OAuthTrustSource.UserEntered,
            SaslMechanism = _authCapabilities.PreferredOAuthMechanism
        };
    }

    /// <summary>
    /// Clears what discovery and sign-in found for the previous address. A session already handed to
    /// the account setup is only forgotten here, because that flow owns it now.
    /// </summary>
    private void ResetSecureSignInState()
    {
        CancelOAuthSignIn();

        if (_isOAuthSessionHandedOff)
            ForgetOAuthSession();
        else
            DiscardOAuthSession();

        _oauthDiscovery = OAuthDiscoveryResult.NotOffered;
        _authCapabilities = ImapAuthCapabilities.Unknown;
        UsePasswordInsteadOfOAuth = false;
        IsIssuerConfirmed = false;
        IsOrganizationSignInExpanded = false;
        CustomOAuthIssuer = string.Empty;
        CustomOAuthClientId = string.Empty;
        CustomOAuthScopes = string.Empty;
        IsOAuthInfoBarOpen = false;
        ResetDavOAuthState();

        UpdateOAuthMethodListing();
        NotifySecureSignInLayoutChanged();
    }

    private void DiscardOAuthSession()
    {
        var session = _oauthSession;
        ForgetOAuthSession();

        if (session != null)
            _oauthTokenService?.DiscardSession(session.Key);
    }

    private void ForgetOAuthSession()
    {
        _isOAuthSessionHandedOff = false;

        if (_oauthSession == null)
            return;

        _oauthSession = null;
        _davGrantedScopes = Array.Empty<string>();
        IsDavScopeSignInRequired = false;
        NotifySecureSignInLayoutChanged();
    }

    /// <summary>
    /// Hands the session to the account setup when a connection uses OAuth 2.0. A session the user
    /// signed in with but no longer uses is discarded, so its tokens never reach disk.
    /// </summary>
    private OAuthSetupSession? TakeOAuthSessionForSave(CustomServerInformation serverInformation)
    {
        if (_oauthSession == null)
            return null;

        if (!UsesOAuth(serverInformation))
        {
            DiscardOAuthSession();
            return null;
        }

        _isOAuthSessionHandedOff = true;
        return _oauthSession;
    }

    private async Task LoadStoredOAuthProfileAsync(MailAccount account)
    {
        _storedOAuthProfile = null;

        if (_accountOAuthProfileService != null && UsesOAuth(account.ServerInformation))
        {
            try
            {
                _storedOAuthProfile = await _accountOAuthProfileService.GetAsync(account.Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read the OAuth profile of account {AccountId}.", account.Id);
            }
        }

        NotifySecureSignInLayoutChanged();
    }

    private Task<ImapConnectivityTestResults> RunImapConnectivityTestAsync(CustomServerInformation serverInformation)
    {
        if (!UsesOAuth(serverInformation))
            return SynchronizationManager.Instance.TestImapConnectivityAsync(serverInformation);

        var tokenService = _oauthTokenService;
        if (tokenService == null || !HasOAuthCredentialForTest)
            throw new InvalidOperationException(Translator.ImapSetup_OAuthSignInRequired);

        var tokenKey = IsCreateMode && _oauthSession != null ? _oauthSession.Key : _editingAccountId;
        return SynchronizationManager.Instance.TestImapConnectivityAsync(serverInformation, tokenService, tokenKey);
    }

    private static bool UsesOAuth(CustomServerInformation? serverInformation)
        => serverInformation != null &&
           (serverInformation.IncomingAuthenticationMethod == ImapAuthenticationMethod.OAuth2 ||
            serverInformation.OutgoingAuthenticationMethod == ImapAuthenticationMethod.OAuth2);

    private void RememberDiscoveredAuthenticationMethods()
    {
        var incoming = GetAuthenticationMethodByIndex(SelectedIncomingServerAuthenticationMethodIndex);
        var outgoing = GetAuthenticationMethodByIndex(SelectedOutgoingServerAuthenticationMethodIndex);

        _discoveredIncomingMethod = incoming == ImapAuthenticationMethod.OAuth2 ? ImapAuthenticationMethod.Auto : incoming;
        _discoveredOutgoingMethod = outgoing == ImapAuthenticationMethod.OAuth2 ? ImapAuthenticationMethod.Auto : outgoing;
    }

    private void SelectOAuthAuthenticationMethods()
    {
        EnsureOAuthMethodListed();

        var index = FindAuthenticationMethodIndex(ImapAuthenticationMethod.OAuth2);
        SelectedIncomingServerAuthenticationMethodIndex = index;
        SelectedOutgoingServerAuthenticationMethodIndex = index;
    }

    private void RestorePasswordAuthenticationMethods()
    {
        if (IsIncomingOAuthSelected)
            SelectedIncomingServerAuthenticationMethodIndex = FindAuthenticationMethodIndex(_discoveredIncomingMethod);

        if (IsOutgoingOAuthSelected)
            SelectedOutgoingServerAuthenticationMethodIndex = FindAuthenticationMethodIndex(_discoveredOutgoingMethod);
    }

    /// <summary>
    /// "OAuth 2.0" joins the method lists only when the server advertised it, a sign-in exists, or
    /// Advanced is open, so password-only servers show the same list as before.
    /// </summary>
    private void UpdateOAuthMethodListing()
    {
        if (ShouldListOAuthMethod)
            EnsureOAuthMethodListed();
        else
            RemoveOAuthMethod();
    }

    private void EnsureOAuthMethodListed()
    {
        // POP3 never signs in with OAuth; its connection policy removes the mechanisms.
        if (IsPop3 || AvailableAuthenticationMethods.Any(item => item.ImapAuthenticationMethod == ImapAuthenticationMethod.OAuth2))
            return;

        AvailableAuthenticationMethods.Add(new ImapAuthenticationMethodModel(ImapAuthenticationMethod.OAuth2, Translator.ImapAuthenticationMethod_OAuth2));
        AvailableAuthenticationMethodDisplayNames.Add(Translator.ImapAuthenticationMethod_OAuth2);
    }

    private void RemoveOAuthMethod()
    {
        var index = AvailableAuthenticationMethods
            .Select((item, itemIndex) => (item, itemIndex))
            .Where(pair => pair.item.ImapAuthenticationMethod == ImapAuthenticationMethod.OAuth2)
            .Select(pair => pair.itemIndex)
            .DefaultIfEmpty(-1)
            .First();

        if (index < 0)
            return;

        AvailableAuthenticationMethods.RemoveAt(index);
        AvailableAuthenticationMethodDisplayNames.RemoveAt(index);
    }

    private void ApplyMailServerAddress()
    {
        var host = MailServerAddress?.Trim();
        if (!IsMailSupportEnabled || string.IsNullOrWhiteSpace(host))
            return;

        IncomingServer = host;
        OutgoingServer = host;

        if (string.IsNullOrWhiteSpace(IncomingServerPort))
            IncomingServerPort = "993";

        if (string.IsNullOrWhiteSpace(OutgoingServerPort))
            OutgoingServerPort = "587";
    }

    private string GetLoginHint()
    {
        var username = IncomingServerUsername?.Trim();
        return string.IsNullOrWhiteSpace(username) ? EmailAddress?.Trim() ?? string.Empty : username;
    }

    private static string GetHost(string? issuer)
        => Uri.TryCreate(issuer, UriKind.Absolute, out var uri) ? uri.Host : issuer ?? string.Empty;

    private void NotifySecureSignInLayoutChanged()
    {
        OnPropertyChanged(nameof(IsSignInIdentityStageVisible));
        OnPropertyChanged(nameof(IsSignInMethodStageVisible));
        OnPropertyChanged(nameof(IsSignInMailServerAddressVisible));
        OnPropertyChanged(nameof(IsOAuthSignedIn));
        OnPropertyChanged(nameof(IsOAuthChoiceVisible));
        OnPropertyChanged(nameof(IsOAuthSignedInVisible));
        OnPropertyChanged(nameof(IsPasswordMethodVisible));
        OnPropertyChanged(nameof(IsUseSecureSignInVisible));
        OnPropertyChanged(nameof(IsOrganizationSignInOptionVisible));
        OnPropertyChanged(nameof(IsOrganizationSignInFieldsVisible));
        OnPropertyChanged(nameof(IsIssuerConfirmationVisible));
        OnPropertyChanged(nameof(IsNoRefreshTokenWarningVisible));
        OnPropertyChanged(nameof(IsPrimaryActionVisible));
        OnPropertyChanged(nameof(SignInAddressSummary));
        OnPropertyChanged(nameof(SecureSignInTitle));
        OnPropertyChanged(nameof(OAuthSignInButtonText));
        OnPropertyChanged(nameof(OAuthIssuerHost));
        OnPropertyChanged(nameof(IssuerConfirmationText));
        OnPropertyChanged(nameof(OAuthSignedInTitle));
        OnPropertyChanged(nameof(OAuthSignedInDescription));
        OnPropertyChanged(nameof(OAuthServerSignInText));
        OnPropertyChanged(nameof(EditOAuthDescription));
        NotifyAuthenticationMethodLayoutChanged();

        SignInWithOAuthCommand.NotifyCanExecuteChanged();
        SignInAgainCommand.NotifyCanExecuteChanged();
        SignInWithOrganizationClientCommand.NotifyCanExecuteChanged();
        ReauthenticateOAuthCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAuthenticationMethodLayoutChanged()
    {
        OnPropertyChanged(nameof(IsIncomingOAuthSelected));
        OnPropertyChanged(nameof(IsOutgoingOAuthSelected));
        OnPropertyChanged(nameof(IsOAuthServerSignInVisible));
        OnPropertyChanged(nameof(IsIncomingPasswordSignInVisible));
        OnPropertyChanged(nameof(IsOutgoingSharedSignInOptionVisible));
        OnPropertyChanged(nameof(IsOutgoingCredentialsVisible));
        OnPropertyChanged(nameof(IsDavCredentialsOptionVisible));
        OnPropertyChanged(nameof(IsDavCredentialsVisible));
        OnPropertyChanged(nameof(IsCardDavCredentialsOptionVisible));
        OnPropertyChanged(nameof(IsCardDavCredentialsVisible));
        OnPropertyChanged(nameof(IsEditOAuthSignInVisible));
        OnPropertyChanged(nameof(IsEditPasswordSignInVisible));
        OnPropertyChanged(nameof(IncomingSummary));
        OnPropertyChanged(nameof(OutgoingSummary));
        NotifyDavSignInLayoutChanged();
    }

    partial void OnSignInStageChanged(ImapSignInStage value) => NotifySecureSignInLayoutChanged();

    partial void OnUsePasswordInsteadOfOAuthChanged(bool value)
    {
        IsOAuthInfoBarOpen = false;
        NotifySecureSignInLayoutChanged();
    }

    partial void OnIsOrganizationSignInExpandedChanged(bool value) => OnPropertyChanged(nameof(IsOrganizationSignInFieldsVisible));

    partial void OnIsAdvancedExpandedChanged(bool value)
    {
        if (value)
            EnsureOAuthMethodListed();
    }

    partial void OnIsOAuthSigningInChanged(bool value) => NotifyOAuthCommandsChanged();

    partial void OnIsBusyChanged(bool value) => NotifyOAuthCommandsChanged();

    partial void OnSelectedIncomingServerAuthenticationMethodIndexChanged(int value) => NotifyAuthenticationMethodLayoutChanged();

    partial void OnSelectedOutgoingServerAuthenticationMethodIndexChanged(int value) => NotifyAuthenticationMethodLayoutChanged();

    private void NotifyOAuthCommandsChanged()
    {
        SignInWithOAuthCommand.NotifyCanExecuteChanged();
        SignInAgainCommand.NotifyCanExecuteChanged();
        SignInWithOrganizationClientCommand.NotifyCanExecuteChanged();
        ReauthenticateOAuthCommand.NotifyCanExecuteChanged();
        ChangeAddressCommand.NotifyCanExecuteChanged();
    }
}
