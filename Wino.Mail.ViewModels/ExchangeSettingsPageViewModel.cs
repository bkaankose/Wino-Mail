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
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

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

    public ExchangeSettingsPageViewModel(
        WelcomeWizardContext wizardContext,
        IInteractiveOidcAuthenticator interactiveOidcAuthenticator,
        IExchangeAutoDiscoveryService autoDiscoveryService,
        IExchangeAuthCapabilityProbe authCapabilityProbe)
    {
        _wizardContext = wizardContext;
        _interactiveOidcAuthenticator = interactiveOidcAuthenticator;
        _autoDiscoveryService = autoDiscoveryService;
        _authCapabilityProbe = authCapabilityProbe;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (string.IsNullOrWhiteSpace(EmailAddress) && !string.IsNullOrWhiteSpace(_wizardContext.EmailAddress))
            EmailAddress = _wizardContext.EmailAddress;
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
            ValidationMessage = "Enter an email address first.";
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
                if (!string.IsNullOrWhiteSpace(discovered))
                    EwsUrl = discovered;
            }

            await DetectAuthMethodAsync();

            if (string.IsNullOrWhiteSpace(EwsUrl))
                StatusMessage = "Couldn't auto-discover the EWS URL — enter it below.";

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
        // When modern auth is on without a (discovered) authority, the user must enter it in Advanced
        // settings — open it so the field is visible instead of hidden behind a collapsed expander.
        if (value && string.IsNullOrWhiteSpace(OAuthAuthority))
            IsAdvancedExpanded = true;
    }

    /// <summary>
    /// Probes the EWS endpoint for modern-auth availability and flips the toggle to match. The probe
    /// is server-level (not per-mailbox), so this is a smart default the user can still override.
    /// </summary>
    private async Task DetectAuthMethodAsync()
    {
        if (string.IsNullOrWhiteSpace(EwsUrl) || !Uri.TryCreate(EwsUrl.Trim(), UriKind.Absolute, out _))
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
                    ? "Modern authentication is available — enter your identity provider's authority in Advanced settings."
                    : $"Modern authentication discovered — using {OAuthAuthority}.";
                break;
            case ExchangeAuthCapability.BasicOnly:
                UseModernAuth = false;
                StatusMessage = "This server offers password (NTLM/Basic) authentication.";
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
            ValidationMessage = "Display name, email address, and EWS URL are required.";
            return;
        }

        if (!Uri.TryCreate(EwsUrl.Trim(), UriKind.Absolute, out _))
        {
            ValidationMessage = "Enter a valid EWS URL, e.g. https://mail.example.com/EWS/Exchange.asmx";
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
                ValidationMessage = "Password is required for NTLM/Basic authentication.";
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

        // Connectivity (and credential validity) is verified by the folder-sync step inside the
        // setup progress page; failures roll the account back.
        _wizardContext.ImapCalDavSetupResult = new ImapCalDavSetupResult
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = EmailAddress.Trim(),
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true, // EWS calendar sync (Phase 2) is implemented
            ServerInformation = serverInformation
        };

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step3Title,
            WinoPage.AccountSetupProgressPage));
    }

    private async Task<CustomServerInformation> BuildModernAuthServerInformationAsync()
    {
        if (string.IsNullOrWhiteSpace(OAuthAuthority))
        {
            ValidationMessage = "Authority URL is required for modern auth (e.g. https://adfs.example.com/adfs).";
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
                ValidationMessage = "Sign-in succeeded but no refresh token was returned (the issuer must grant offline_access).";
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
            ValidationMessage = $"Sign-in failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
