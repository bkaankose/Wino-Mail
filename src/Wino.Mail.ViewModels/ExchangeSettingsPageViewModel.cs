using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Authentication.Oidc;
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

/// <summary>Collects the on-premises Exchange endpoint, sign-in and connection-method settings.</summary>
public partial class ExchangeSettingsPageViewModel : MailBaseViewModel
{
    private const string DefaultEwsClientId = "00000002-0000-0ff1-ce00-000000000000";

    private readonly WelcomeWizardContext _wizardContext;
    private readonly IInteractiveOidcAuthenticator _interactiveOidcAuthenticator;
    private readonly IExchangeAutoDiscoveryService _autoDiscoveryService;
    private readonly IExchangeAuthCapabilityProbe _authCapabilityProbe;
    private readonly IAccountService _accountService;
    private readonly IMapiConnectionProbe _mapiConnectionProbe;

    private Guid? _editingAccountId;

    // Server information produced by an interactive OAuth sign-in during "Test MAPI", kept so Save reuses
    // it instead of prompting again. The probe's token refresh rotates the refresh token onto this same
    // object, so reusing it is also what keeps the newest token. Dropped when the inputs change.
    private CustomServerInformation _preparedModernAuthServerInformation;
    private string _preparedModernAuthKey;
    private CancellationTokenSource _signInCancellationTokenSource;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmailAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EwsUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuth))]
    public partial bool UseModernAuth { get; set; }

    [ObservableProperty]
    public partial int AuthMethodIndex { get; set; }

    /// <summary>Index into the connection-method list; the values are <see cref="ExchangeTransport"/> in order.</summary>
    [ObservableProperty]
    public partial int TransportIndex { get; set; }

    [ObservableProperty]
    public partial string OAuthAuthority { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OAuthClientId { get; set; } = DefaultEwsClientId;

    [ObservableProperty]
    public partial string OAuthResource { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OAuthRedirectUri { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
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

    public bool IsPasswordAuth => !UseModernAuth;
    public bool IsIdle => !IsBusy;

    public ExchangeSettingsPageViewModel(
        WelcomeWizardContext wizardContext,
        IInteractiveOidcAuthenticator interactiveOidcAuthenticator,
        IExchangeAutoDiscoveryService autoDiscoveryService,
        IExchangeAuthCapabilityProbe authCapabilityProbe,
        IAccountService accountService,
        IMapiConnectionProbe mapiConnectionProbe)
    {
        _mapiConnectionProbe = mapiConnectionProbe;
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

        if (string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(_wizardContext.DisplayName))
            DisplayName = _wizardContext.DisplayName;
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        _signInCancellationTokenSource?.Cancel();
        base.OnNavigatedFrom(mode, parameters);
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
        TransportIndex = (int)server.ExchangeTransport;

        if (!string.IsNullOrWhiteSpace(server.IncomingServerUsername) &&
            !string.Equals(server.IncomingServerUsername, account.Address, StringComparison.OrdinalIgnoreCase))
        {
            Username = server.IncomingServerUsername;
        }

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

        var serverInformation = await BuildServerInformationAsync();
        if (serverInformation == null)
            return; // ValidationMessage already set

        await DetectTransportAsync(serverInformation);

        if (_editingAccountId is Guid editingAccountId)
        {
            await SaveEditedAccountAsync(editingAccountId, serverInformation);
            return;
        }

        // The calendar is served by the Exchange synchronizer when the wizard chose the provider source;
        // contacts and tasks take their grants from the wizard context when the account is created.
        _wizardContext.ImapCalDavSetupResult = new ImapCalDavSetupResult
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = EmailAddress.Trim(),
            IsMailAccessGranted = _wizardContext.IsMailAccessEnabled,
            IsCalendarAccessGranted = _wizardContext.IsCalendarAccessEnabled &&
                _wizardContext.CalendarIntegrationSource == AccountIntegrationSource.Provider,
            ShouldAppendMessagesToSentFolder = false,
            ServerInformation = serverInformation
        };

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step3Title,
            WinoPage.AccountSetupProgressPage));
    }

    /// <summary>
    /// Builds the server information the form describes, signing in interactively for modern auth
    /// (once: a result prepared by the MAPI test is reused while the inputs are unchanged).
    /// </summary>
    private async Task<CustomServerInformation> BuildServerInformationAsync()
    {
        if (UseModernAuth)
        {
            var key = string.Join("|", EmailAddress?.Trim(), EwsUrl?.Trim(), OAuthAuthority?.Trim(), OAuthClientId?.Trim(), OAuthResource?.Trim(), OAuthRedirectUri?.Trim(), GetEffectiveUsername());
            if (_preparedModernAuthServerInformation != null && _preparedModernAuthKey == key)
                return _preparedModernAuthServerInformation;

            var built = await BuildModernAuthServerInformationAsync();
            if (built != null)
            {
                _preparedModernAuthServerInformation = built;
                _preparedModernAuthKey = key;
            }

            return built;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_PasswordRequired;
            return null;
        }

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            Address = EmailAddress.Trim(),
            IncomingServer = EwsUrl.Trim(),
            IncomingServerType = CustomIncomingServerType.Exchange,
            ExchangeTransport = (ExchangeTransport)TransportIndex,
            IncomingServerUsername = GetEffectiveUsername(),
            IncomingServerPassword = Password,
            CalendarSupportMode = ResolveCalendarSupportMode()
        };
    }

    // The local calendar the wizard offered is created by the account service when the mode is LocalOnly.
    private ImapCalendarSupportMode ResolveCalendarSupportMode()
        => _editingAccountId == null && _wizardContext.IsCalendarAccessEnabled
            ? ImapCalendarSupportMode.LocalOnly
            : ImapCalendarSupportMode.Disabled;

    /// <summary>
    /// Proves the native MAPI/HTTP path with exactly the credentials this form would save, before the
    /// account exists. Read-only against the server; the account is not created.
    /// </summary>
    [RelayCommand]
    private async Task TestMapiConnectionAsync()
    {
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(EmailAddress) || !IsHttpsUrl(EwsUrl))
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_RequiredFields;
            return;
        }

        var serverInformation = await BuildServerInformationAsync();
        if (serverInformation == null)
            return;

        IsBusy = true;
        StatusMessage = Translator.SettingsEditAccountDetails_MapiProbe_Running;

        try
        {
            var result = await _mapiConnectionProbe.ProbeAsync(CreateTransientAccount(serverInformation, Guid.NewGuid()));

            if (result.Succeeded)
            {
                StatusMessage = MapiProbeMessages.Success(result);
            }
            else
            {
                StatusMessage = string.Empty;
                ValidationMessage = MapiProbeMessages.Failure(result);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// With the connection method on Automatic, asks Autodiscover whether the mailbox offers MAPI/HTTP
    /// and records the answer on the server information about to be saved. Unreachable Autodiscover
    /// leaves it undecided: the MAPI path tries first and records a fallback itself.
    /// </summary>
    private async Task DetectTransportAsync(CustomServerInformation serverInformation)
    {
        if (serverInformation.ExchangeTransport != ExchangeTransport.Automatic)
        {
            serverInformation.DetectedExchangeTransport = ExchangeTransport.Automatic;
            return;
        }

        IsBusy = true;
        StatusMessage = Translator.ExchangeSettingsPage_Status_DetectingTransport;
        try
        {
            var account = CreateTransientAccount(serverInformation, _editingAccountId ?? Guid.NewGuid());

            serverInformation.DetectedExchangeTransport = await _mapiConnectionProbe.DetectTransportAsync(account);
            StatusMessage = serverInformation.DetectedExchangeTransport switch
            {
                ExchangeTransport.MapiHttp => Translator.ExchangeSettingsPage_Status_TransportMapi,
                ExchangeTransport.Ews => Translator.ExchangeSettingsPage_Status_TransportEws,
                _ => Translator.ExchangeSettingsPage_Status_TransportUndecided
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The account the probe and the transport detection run against: it is never saved. The
    /// authenticators only read its server information and use the id as a token cache key.
    /// </summary>
    private MailAccount CreateTransientAccount(CustomServerInformation serverInformation, Guid id) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress.Trim() : DisplayName.Trim(),
        Address = EmailAddress.Trim(),
        ProviderType = MailProviderType.Exchange,
        ServerInformation = serverInformation
    };

    private async Task SaveEditedAccountAsync(Guid accountId, CustomServerInformation serverInformation)
    {
        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
        {
            ValidationMessage = Translator.ExchangeSettingsPage_Validation_AccountNotFound;
            return;
        }

        var previousTransport = account.ServerInformation?.EffectiveExchangeTransport;
        serverInformation.Id = account.ServerInformation?.Id ?? Guid.NewGuid();
        serverInformation.AccountId = account.Id;
        serverInformation.CalendarSupportMode = account.ServerInformation?.CalendarSupportMode ?? ImapCalendarSupportMode.Disabled;

        account.SenderName = DisplayName.Trim();
        account.Address = EmailAddress.Trim();
        account.ServerInformation = serverInformation;
        account.AttentionReason = AccountAttentionReason.None;

        await _accountService.UpdateAccountCustomServerInformationAsync(serverInformation);
        await _accountService.UpdateAccountAsync(account);

        // The two transports key folders differently (MAPI re-keys EWS rows in place, EWS does not
        // know MAPI keys), so a switch starts the mailbox cache over rather than leaving stale rows.
        if (previousTransport is not null && previousTransport != serverInformation.EffectiveExchangeTransport)
            await _accountService.DeleteAccountMailCacheAsync(account.Id, AccountCacheResetReason.ExpiredCache);

        // Settings edits do not trigger synchronizer refresh, so rebuild before syncing.
        await SynchronizationManager.Instance.DestroySynchronizerAsync(account.Id).ConfigureAwait(false);

        Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }));

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    private string GetEffectiveUsername()
        => string.IsNullOrWhiteSpace(Username) ? EmailAddress.Trim() : Username.Trim();

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
        _signInCancellationTokenSource?.Cancel();
        var signInCancellationTokenSource = new CancellationTokenSource();
        _signInCancellationTokenSource = signInCancellationTokenSource;

        try
        {
            var tokenSet = await _interactiveOidcAuthenticator.SignInAsync(configuration, signInCancellationTokenSource.Token);

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
                ExchangeTransport = (ExchangeTransport)TransportIndex,
                IncomingServerUsername = GetEffectiveUsername(),
                CalendarSupportMode = ResolveCalendarSupportMode(),
                UseOAuthAuthentication = true,
                OAuthAuthority = configuration.Authority,
                OAuthClientId = configuration.ClientId,
                OAuthResource = configuration.Resource,
                OAuthRedirectUri = configuration.RedirectUri,
                OAuthRefreshToken = tokenSet.RefreshToken
            };
        }
        catch (OperationCanceledException) when (signInCancellationTokenSource.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ValidationMessage = string.Format(Translator.ExchangeSettingsPage_Validation_SignInFailed, ex.Message);
            return null;
        }
        finally
        {
            if (ReferenceEquals(_signInCancellationTokenSource, signInCancellationTokenSource))
            {
                _signInCancellationTokenSource = null;
            }

            signInCancellationTokenSource.Dispose();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Messenger.Send(new BackBreadcrumNavigationRequested());
}
