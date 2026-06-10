using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Authentication;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels;

/// <summary>
/// On-premises Exchange (EWS) account-setup page. Collects the EWS endpoint and, per the
/// chosen auth method, either NTLM/Basic credentials or modern auth (OAuth2). For modern auth
/// it runs an interactive sign-in to obtain a refresh token, then reuses the shared account-creation
/// flow (the account is created with ProviderType.Exchange + CustomServerInformation by
/// AccountSetupProgressPageViewModel).
/// </summary>
public partial class ExchangeSettingsPageViewModel : MailBaseViewModel
{
    // Well-known EWS client id (generic, not deployment-specific).
    private const string DefaultEwsClientId = "00000002-0000-0ff1-ce00-000000000000";

    private readonly WelcomeWizardContext _wizardContext;
    private readonly IInteractiveOidcAuthenticator _interactiveOidcAuthenticator;
    private readonly IExchangeAutoDiscoveryService _autoDiscoveryService;
    private readonly IExchangeAuthCapabilityProbe _authCapabilityProbe;
    private readonly IAccountService _accountService;

    /// <summary>Set when the page is opened to edit an existing account's server/sign-in settings.</summary>
    private Guid? _editingAccountId;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmailAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EwsUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>True = modern auth (OAuth2); false = NTLM/Basic password.</summary>
    [ObservableProperty]
    public partial bool UseModernAuth { get; set; }

    /// <summary>
    /// Authentication-method picker selection: 0 = password (NTLM/Basic), 1 = modern auth (OAuth).
    /// Kept in sync with <see cref="UseModernAuth"/> in both directions so the dropdown and the
    /// detection logic share one source of truth.
    /// </summary>
    [ObservableProperty]
    public partial int AuthMethodIndex { get; set; }

    [ObservableProperty]
    public partial string OAuthAuthority { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OAuthClientId { get; set; } = DefaultEwsClientId;

    [ObservableProperty]
    public partial string OAuthResource { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OAuthRedirectUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True once Discover has run — reveals the connection fields and the Save button.</summary>
    [ObservableProperty]
    public partial bool IsDiscovered { get; set; }

    /// <summary>Whether Advanced settings is expanded; auto-opened when the authority must be entered by hand.</summary>
    [ObservableProperty]
    public partial bool IsAdvancedExpanded { get; set; }

    [ObservableProperty]
    public partial string ValidationMessage { get; set; } = string.Empty;

    /// <summary>Neutral informational text (e.g. auto-detection results), distinct from errors.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>True when editing an existing account (vs. onboarding a new one).</summary>
    [ObservableProperty]
    public partial bool IsEditMode { get; set; }

    public ExchangeSettingsPageViewModel(
        WelcomeWizardContext wizardContext,
        IInteractiveOidcAuthenticator interactiveOidcAuthenticator,
        IExchangeAutoDiscoveryService autoDiscoveryService,
        IExchangeAuthCapabilityProbe authCapabilityProbe,
        IAccountService accountService)
    {
        _wizardContext = wizardContext;
        _interactiveOidcAuthenticator = interactiveOidcAuthenticator;
        _autoDiscoveryService = autoDiscoveryService;
        _authCapabilityProbe = authCapabilityProbe;
        _accountService = accountService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        // Edit mode: opened from account settings with the account id. Load the existing
        // server/sign-in settings so the user can update the EWS URL or re-enter credentials.
        if (parameters is Guid accountId)
        {
            await LoadExistingAccountAsync(accountId);
            return;
        }

        if (string.IsNullOrWhiteSpace(EmailAddress) && !string.IsNullOrWhiteSpace(_wizardContext.EmailAddress))
            EmailAddress = _wizardContext.EmailAddress;
    }

    private async Task LoadExistingAccountAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId);
        if (account?.ServerInformation == null)
            return;

        var server = account.ServerInformation;

        _editingAccountId = accountId;
        IsEditMode = true;

        DisplayName = account.SenderName ?? account.Name ?? string.Empty;
        EmailAddress = account.Address ?? server.IncomingServerUsername ?? string.Empty;
        EwsUrl = server.IncomingServer ?? string.Empty;

        if (server.UseOAuthAuthentication)
        {
            OAuthAuthority = server.OAuthAuthority ?? string.Empty;
            OAuthClientId = string.IsNullOrWhiteSpace(server.OAuthClientId) ? DefaultEwsClientId : server.OAuthClientId;
            OAuthResource = server.OAuthResource ?? string.Empty;
            OAuthRedirectUri = server.OAuthRedirectUri ?? string.Empty;
            UseModernAuth = true;
        }
        else
        {
            UseModernAuth = false;
        }

        // Fields are already known for an existing account — reveal them without requiring Discover.
        IsDiscovered = true;
        StatusMessage = UseModernAuth
            ? Translator.ExchangeSettingsPage_Status_EditModernAuth
            : Translator.ExchangeSettingsPage_Status_EditPassword;
    }

    /// <summary>
    /// Auto-fills the EWS URL from the email address via anonymous Autodiscover V2, then probes the
    /// resolved endpoint and defaults the auth method to modern auth when the server offers it.
    /// </summary>
    [RelayCommand]
    private async Task DiscoverEwsUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(EmailAddress))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_EmailRequired;
            return;
        }

        IsBusy = true;
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            // Only run URL discovery when the field is empty; if the user already typed an EWS URL,
            // skip straight to detecting its auth method (avoids a pointless Autodiscover round-trip).
            if (string.IsNullOrWhiteSpace(EwsUrl))
            {
                var discovered = await _autoDiscoveryService.TryDiscoverEwsUrlAsync(EmailAddress.Trim());

                // Only accept an HTTPS endpoint from autodiscovery — never store/probe an http URL.
                if (!string.IsNullOrWhiteSpace(discovered) && IsHttpsUrl(discovered))
                    EwsUrl = discovered;
            }

            await DetectAuthMethodAsync();

            if (string.IsNullOrWhiteSpace(EwsUrl))
                StatusMessage = Translator.ExchangeSettingsPage_Status_EwsUrlNotDiscovered;

            // Reveal the remaining fields (filled from discovery, or for manual entry on fallback).
            IsDiscovered = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnUseModernAuthChanged(bool value)
    {
        // Mirror into the picker index. CommunityToolkit suppresses the change notification when the
        // value is unchanged, so this can't loop with OnAuthMethodIndexChanged.
        AuthMethodIndex = value ? 1 : 0;

        // When modern auth is on without a (discovered) authority, the user must enter it in Advanced
        // settings — open it so the field is visible instead of hidden behind a collapsed expander.
        if (value && string.IsNullOrWhiteSpace(OAuthAuthority))
            IsAdvancedExpanded = true;
    }

    partial void OnAuthMethodIndexChanged(int value) => UseModernAuth = value == 1;

    /// <summary>
    /// Probes the EWS endpoint for modern-auth availability and flips the toggle to match. The probe
    /// is server-level (not per-mailbox), so this is a smart default the user can still override.
    /// </summary>
    private async Task DetectAuthMethodAsync()
    {
        // Never probe a non-HTTPS endpoint — the probe carries the mailbox identity header.
        if (!IsHttpsUrl(EwsUrl))
            return;

        var probe = await _authCapabilityProbe.ProbeAsync(EwsUrl.Trim(), EmailAddress?.Trim());
        switch (probe.Capability)
        {
            case ExchangeAuthCapability.ModernAuthAvailable:
                // Set the discovered authority BEFORE flipping the toggle, so the toggle handler can
                // decide whether to auto-expand Advanced (only when the authority is still empty).
                if (!string.IsNullOrWhiteSpace(probe.Authority) && string.IsNullOrWhiteSpace(OAuthAuthority))
                    OAuthAuthority = probe.Authority;

                UseModernAuth = true;

                StatusMessage = string.IsNullOrWhiteSpace(OAuthAuthority)
                    ? Translator.ExchangeSettingsPage_Status_ModernAuthAvailableNoAuthority
                    : string.Format(Translator.ExchangeSettingsPage_Status_ModernAuthDiscovered, OAuthAuthority);
                break;
            case ExchangeAuthCapability.BasicOnly:
                UseModernAuth = false;
                StatusMessage = Translator.ExchangeSettingsPage_Status_BasicOnly;
                break;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(DisplayName) ||
            string.IsNullOrWhiteSpace(EmailAddress) ||
            string.IsNullOrWhiteSpace(EwsUrl))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_RequiredFields;
            return;
        }

        if (!IsHttpsUrl(EwsUrl))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_EwsUrlInvalid;
            return;
        }

        CustomServerInformation serverInformation;

        if (UseModernAuth)
        {
            serverInformation = await BuildModernAuthServerInformationAsync();
            if (serverInformation == null)
                return; // ValidationMessage already set
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Password))
            {
                ValidationMessage = Translator.ExchangeSettingsPage_Validation_PasswordRequired;
                return;
            }

            serverInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                Address = EmailAddress.Trim(),
                IncomingServer = EwsUrl.Trim(),
                IncomingServerType = CustomIncomingServerType.Exchange,
                IncomingServerUsername = EmailAddress.Trim(),
                IncomingServerPassword = Password,
                CalendarSupportMode = ImapCalendarSupportMode.Disabled
            };
        }

        if (_editingAccountId is Guid editingAccountId)
        {
            await SaveEditedAccountAsync(editingAccountId, serverInformation);
            return;
        }

        // Connectivity (and credential validity) is verified by the folder-sync step inside the
        // setup progress page; failures roll the account back.
        _wizardContext.ImapCalDavSetupResult = new ImapCalDavSetupResult
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = EmailAddress.Trim(),
            IsMailAccessGranted = _wizardContext.IsMailAccessEnabled,
            IsCalendarAccessGranted = _wizardContext.IsCalendarAccessEnabled,
            ServerInformation = serverInformation
        };

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step3Title,
            WinoPage.AccountSetupProgressPage));
    }

    /// <summary>
    /// Persists updated server/sign-in settings onto an existing account, clears any pending
    /// credential-attention flag, and kicks a fresh folder sync to validate the new settings.
    /// </summary>
    private async Task SaveEditedAccountAsync(Guid accountId, CustomServerInformation serverInformation)
    {
        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_AccountNotFound;
            return;
        }

        serverInformation.Id = account.ServerInformation?.Id ?? Guid.NewGuid();
        serverInformation.AccountId = account.Id;

        account.SenderName = DisplayName.Trim();
        account.Address = EmailAddress.Trim();
        account.ServerInformation = serverInformation;
        account.AttentionReason = AccountAttentionReason.None;

        await _accountService.UpdateAccountCustomServerInformationAsync(serverInformation);
        await _accountService.UpdateAccountAsync(account);

        // Drop the cached synchronizer so it is rebuilt with the new endpoint/credentials/token.
        // SynchronizationManager only refreshes synchronizers on access-flag changes, so an edited
        // EWS URL, password, or OAuth config would otherwise not take effect until app restart.
        await SynchronizationManager.Instance.DestroySynchronizerAsync(account.Id).ConfigureAwait(false);

        // Re-sync folders so the updated endpoint/credentials are exercised immediately.
        Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }));

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    /// <summary>Whether <paramref name="url"/> is a well-formed absolute HTTPS URL.</summary>
    private static bool IsHttpsUrl(string url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Whether <paramref name="url"/> is an http loopback address (used by the loopback OAuth flow).</summary>
    private static bool IsLoopbackUrl(string url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;

    private async Task<CustomServerInformation> BuildModernAuthServerInformationAsync()
    {
        if (string.IsNullOrWhiteSpace(OAuthAuthority))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_AuthorityRequired;
            return null;
        }

        if (!IsHttpsUrl(OAuthAuthority))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_AuthorityNotHttps;
            return null;
        }

        var ewsOrigin = new Uri(EwsUrl.Trim()).GetLeftPart(UriPartial.Authority);

        // Default the protected resource to the EWS server origin when not overridden.
        var resource = string.IsNullOrWhiteSpace(OAuthResource)
            ? ewsOrigin + "/"
            : OAuthResource.Trim();

        // The embedded WebView2 reuses a redirect the IdP already trusts and intercepts it in-process,
        // so the user need not register or type one. Default to the conventional OWA reply URL on the
        // EWS host; the Advanced field lets a non-conventional deployment override it.
        var redirectUri = string.IsNullOrWhiteSpace(OAuthRedirectUri)
            ? ewsOrigin + "/owa/"
            : OAuthRedirectUri.Trim();

        if (!IsHttpsUrl(resource))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_ResourceNotHttps;
            return null;
        }

        // The redirect must be HTTPS, except a loopback address (used by the non-WebView2 flow).
        if (!IsHttpsUrl(redirectUri) && !IsLoopbackUrl(redirectUri))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_RedirectUriNotHttps;
            return null;
        }

        var configuration = new OidcConfiguration
        {
            Authority = OAuthAuthority.Trim(),
            ClientId = OAuthClientId.Trim(),
            Resource = resource,
            RedirectUri = redirectUri
        };

        IsBusy = true;

        try
        {
            var tokenSet = await _interactiveOidcAuthenticator.SignInAsync(configuration);

            if (string.IsNullOrEmpty(tokenSet.RefreshToken))
            {
                ValidationMessage = Translator.ExchangeSettingsPage_Validation_NoRefreshToken;
                return null;
            }

            return new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                Address = EmailAddress.Trim(),
                IncomingServer = EwsUrl.Trim(),
                IncomingServerType = CustomIncomingServerType.Exchange,
                IncomingServerUsername = EmailAddress.Trim(),
                CalendarSupportMode = ImapCalendarSupportMode.Disabled,
                UseOAuthAuthentication = true,
                OAuthAuthority = configuration.Authority,
                OAuthClientId = configuration.ClientId,
                OAuthResource = configuration.Resource,
                OAuthRedirectUri = configuration.RedirectUri,
                OAuthRefreshToken = tokenSet.RefreshToken
            };
        }
        catch (Exception ex)
        {
            ValidationMessage = string.Format(Translator.ExchangeSettingsPage_Validation_SignInFailed, ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
