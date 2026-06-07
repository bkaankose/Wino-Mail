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
            var discovered = await _autoDiscoveryService.TryDiscoverEwsUrlAsync(EmailAddress.Trim());
            if (!string.IsNullOrWhiteSpace(discovered))
                EwsUrl = discovered;
            else if (string.IsNullOrWhiteSpace(EwsUrl))
            {
                ValidationMessage = "Couldn't auto-discover the EWS URL. Enter it manually.";
                return;
            }

            await DetectAuthMethodAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Probes the EWS endpoint for modern-auth availability and flips the toggle to match. The probe
    /// is server-level (not per-mailbox), so this is a smart default the user can still override.
    /// </summary>
    private async Task DetectAuthMethodAsync()
    {
        if (string.IsNullOrWhiteSpace(EwsUrl) || !Uri.TryCreate(EwsUrl.Trim(), UriKind.Absolute, out _))
            return;

        var capability = await _authCapabilityProbe.ProbeAsync(EwsUrl.Trim());
        switch (capability)
        {
            case ExchangeAuthCapability.ModernAuthAvailable:
                UseModernAuth = true;
                StatusMessage = "Modern authentication is available — enabled it for you. You can switch to a password if this mailbox uses one.";
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
            IsCalendarAccessGranted = false, // EWS calendar arrives in Phase 2
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

        if (string.IsNullOrWhiteSpace(OAuthRedirectUri))
        {
            ValidationMessage = "Redirect URI is required — use a reply URL your identity provider already trusts (e.g. your OWA URL).";
            return null;
        }

        // Default the protected resource to the EWS server origin when not overridden.
        var resource = string.IsNullOrWhiteSpace(OAuthResource)
            ? new Uri(EwsUrl.Trim()).GetLeftPart(UriPartial.Authority) + "/"
            : OAuthResource.Trim();

        var configuration = new OidcConfiguration
        {
            Authority = OAuthAuthority.Trim(),
            ClientId = OAuthClientId.Trim(),
            Resource = resource,
            RedirectUri = OAuthRedirectUri.Trim()
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
