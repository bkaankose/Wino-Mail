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

/// <summary>Collects Exchange/EWS endpoint and sign-in settings.</summary>
public partial class ExchangeSettingsPageViewModel : MailBaseViewModel
{
    private const string DefaultEwsClientId = "00000002-0000-0ff1-ce00-000000000000";

    private readonly WelcomeWizardContext _wizardContext;
    private readonly IInteractiveOidcAuthenticator _interactiveOidcAuthenticator;
    private readonly IExchangeAutoDiscoveryService _autoDiscoveryService;
    private readonly IExchangeAuthCapabilityProbe _authCapabilityProbe;
    private readonly IAccountService _accountService;

    private Guid? _editingAccountId;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmailAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EwsUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseModernAuth { get; set; }

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

    [ObservableProperty]
    public partial bool IsDiscovered { get; set; }

    [ObservableProperty]
    public partial bool IsAdvancedExpanded { get; set; }

    [ObservableProperty]
    public partial string ValidationMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

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

        IsDiscovered = true;
        StatusMessage = UseModernAuth
            ? Translator.ExchangeSettingsPage_Status_EditModernAuth
            : Translator.ExchangeSettingsPage_Status_EditPassword;
    }

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
            if (string.IsNullOrWhiteSpace(EwsUrl))
            {
                var discovered = await _autoDiscoveryService.TryDiscoverEwsUrlAsync(EmailAddress.Trim());

                if (!string.IsNullOrWhiteSpace(discovered) && IsHttpsUrl(discovered))
                    EwsUrl = discovered;
            }

            await DetectAuthMethodAsync();

            if (string.IsNullOrWhiteSpace(EwsUrl))
                StatusMessage = Translator.ExchangeSettingsPage_Status_EwsUrlNotDiscovered;

            IsDiscovered = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnUseModernAuthChanged(bool value)
    {
        AuthMethodIndex = value ? 1 : 0;

        // Make the required authority field visible when discovery could not fill it.
        if (value && string.IsNullOrWhiteSpace(OAuthAuthority))
            IsAdvancedExpanded = true;
    }

    partial void OnAuthMethodIndexChanged(int value) => UseModernAuth = value == 1;

    private async Task DetectAuthMethodAsync()
    {
        if (!IsHttpsUrl(EwsUrl))
            return;

        var probe = await _authCapabilityProbe.ProbeAsync(EwsUrl.Trim(), EmailAddress?.Trim());
        switch (probe.Capability)
        {
            case ExchangeAuthCapability.ModernAuthAvailable:
                // Set the authority before toggling, so Advanced only expands when it is still missing.
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

        // Settings edits do not trigger synchronizer refresh, so rebuild before syncing.
        await SynchronizationManager.Instance.DestroySynchronizerAsync(account.Id).ConfigureAwait(false);

        Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }));

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    private static bool IsHttpsUrl(string url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

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

        var resource = string.IsNullOrWhiteSpace(OAuthResource)
            ? ewsOrigin + "/"
            : OAuthResource.Trim();

        // Default to the conventional OWA reply URL; non-standard deployments can override it.
        var redirectUri = string.IsNullOrWhiteSpace(OAuthRedirectUri)
            ? ewsOrigin + "/owa/"
            : OAuthRedirectUri.Trim();

        if (!IsHttpsUrl(resource))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_ResourceNotHttps;
            return null;
        }

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
