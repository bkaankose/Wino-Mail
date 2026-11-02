#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>Address resolution, the discovery step, and the Gmail and Outlook sign-in.</summary>
public partial class AccountSetupPageViewModel
{
    /// <summary>Domain table and catalog routes resolve within this time and never show the discovery step.</summary>
    private static readonly TimeSpan DiscoveryGrace = TimeSpan.FromMilliseconds(300);

    private AccountRouteResolution? _resolution;
    private int _resolveVersion;

    private MailAccount? _transientAccount;
    private TokenInformationEx? _nativeToken;
    private string? _confirmedAddress;

    [ObservableProperty]
    public partial bool IsResolveSlow { get; set; }

    [ObservableProperty]
    public partial bool IsAskingMicrosoft { get; set; }

    [ObservableProperty]
    public partial string DiscoveryHelpText { get; set; } = string.Empty;

    public string DiscoveryText => string.Format(_forcedChoice?.Kind switch
    {
        AccountSetupProviderChoiceKind.Exchange => Translator.AccountSetup_Discovery_ExchangeText,
        AccountSetupProviderChoiceKind.Imap => Translator.AccountSetup_Discovery_ImapText,
        _ => Translator.AccountSetup_Discovery_Text
    }, AddressDomain);
    public string DiscoverySlowText => Translator.AccountSetup_Discovery_Slow;
    public string AskingMicrosoftText => string.Format(Translator.AccountSetup_Discovery_AskingMicrosoft, AddressDomain);

    public AccountRoute Route => _resolution?.Route ?? AccountRoute.NotFound;

    public bool IsResolvingVisible => Phase == AccountSetupPhase.Resolving;
    public bool IsNotFoundVisible => Phase == AccountSetupPhase.NotFound;
    public string NotFoundMessage => string.Format(
        IsExchangeRediscovery ? Translator.AccountSetup_NotFound_ExchangeMessage : Translator.AccountSetup_NotFoundMessage,
        _resolution?.Domain ?? AddressDomain);

    public MailProviderType RouteProviderType => Route switch
    {
        AccountRoute.Gmail => MailProviderType.Gmail,
        AccountRoute.Outlook => MailProviderType.Outlook,
        AccountRoute.Exchange => MailProviderType.Exchange,
        _ => MailProviderType.IMAP4
    };

    public SpecialImapProvider RouteSpecialImapProvider => _resolution?.Imap?.Provider?.SpecialImapProvider ?? SpecialImapProvider.None;

    public string RouteEvidenceText => GetRouteEvidence();

    /// <summary>The provider as the known-so-far card names it: "Gmail", "Outlook", the catalog name or the mail server.</summary>
    public string RouteShortTitle
    {
        get
        {
            if (IsLocalOnlyMode)
                return Translator.AccountSetup_Known_ThisDevice;

            var resolution = _resolution;
            if (resolution == null || !IsRouteFound)
                return string.Empty;

            return resolution.Route switch
            {
                AccountRoute.Gmail or AccountRoute.Outlook => NativeProviderName,
                AccountRoute.Exchange => ExchangeProviderName,
                _ => string.IsNullOrWhiteSpace(resolution.ProviderDisplayName) ? ImapHostName : resolution.ProviderDisplayName
            };
        }
    }

    public bool HasRouteShortTitle => !string.IsNullOrWhiteSpace(RouteShortTitle);

    private bool IsRouteFound => Route is AccountRoute.Gmail or AccountRoute.Outlook or AccountRoute.Imap or AccountRoute.Exchange;
    private bool IsNativeRoute => Route is AccountRoute.Gmail or AccountRoute.Outlook;

    private string AddressDomain => MailAccountAddressValidator.TryGetDomain(EmailAddress?.Trim() ?? string.Empty, out var domain)
        ? domain
        : EmailAddress?.Trim() ?? string.Empty;

    private string NativeProviderName => GetProviderName(RouteProviderType);

    [RelayCommand]
    private async Task ResolveAgainAsync()
    {
        var address = EmailAddress?.Trim() ?? string.Empty;
        if (!MailAccountAddressValidator.IsValid(address))
        {
            Phase = AccountSetupPhase.Identity;
            return;
        }

        await ResolveAsync(address, _forcedChoice);
    }

    [RelayCommand]
    private Task CancelDiscoveryToAdvancedAsync() => OpenAdvancedSetupAsync();

    /// <param name="forcedChoice">The user's provider choice, which replaces detection; null detects the route.</param>
    private async Task ResolveAsync(string address, AccountSetupProviderChoice? forcedChoice = null)
    {
        var operation = StartOperation();

        ClearRoute();
        _forcedChoice = forcedChoice;
        var version = ++_resolveVersion;
        Phase = AccountSetupPhase.Resolving;
        NotifyRoutingLayoutChanged();
        _ = ShowDiscoveryAfterGraceAsync(version);

        var progress = new Progress<AccountRouteProgress>(update => OnResolveProgress(version, update));
        AccountRouteResolution resolution;

        try
        {
            resolution = await _routeResolver.ResolveAsync(new AccountRouteRequest
            {
                Address = address,
                AllowBrowserSignIn = IsSecureSignInSupportedByHost && _oauthTokenService != null,
                ForceRoute = forcedChoice?.Route,
                ForcedImapProvider = forcedChoice?.SpecialImapProvider ?? SpecialImapProvider.None
            }, progress, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Resolving the account route failed.");
            resolution = new AccountRouteResolution { Address = address, Domain = AddressDomain, Route = AccountRoute.NotFound };
        }

        if (operation.IsCancellationRequested || version != _resolveVersion)
            return;

        _resolution = resolution;
        NotifyRoutingLayoutChanged();

        if (!IsRouteFound || (resolution.Route == AccountRoute.Imap && resolution.Imap?.IsFound != true))
        {
            Phase = AccountSetupPhase.NotFound;
            return;
        }

        await ApplyDerivedAccountNameAsync(resolution);

        if (operation.IsCancellationRequested || version != _resolveVersion)
            return;

        ConfigureFeatures(resolution);
        FeatureStep = AccountSetupStep.Mail;
        Phase = AccountSetupPhase.Features;
    }

    private async Task ShowDiscoveryAfterGraceAsync(int version)
    {
        await Task.Delay(DiscoveryGrace);
        ShowDiscovery(version);
    }

    private void ShowDiscovery(int version)
    {
        if (version != _resolveVersion || Phase != AccountSetupPhase.Resolving || _isDiscoveryShown)
            return;

        _isDiscoveryShown = true;
        NotifyStepChanged();
    }

    private void OnResolveProgress(int version, AccountRouteProgress update)
    {
        if (version != _resolveVersion || Phase != AccountSetupPhase.Resolving)
            return;

        ShowDiscovery(version);

        if (update.IsSlow)
            IsResolveSlow = true;

        if (update.Stage == AccountRouteResolveStage.AskingMicrosoft)
            IsAskingMicrosoft = true;

        DiscoveryHelpText = GetStageText(update);
    }

    private static string GetStageText(AccountRouteProgress update) => update.Stage switch
    {
        AccountRouteResolveStage.CheckingDomainHost => string.Format(Translator.AccountSetup_Resolve_CheckingDomainHost, update.Domain),
        AccountRouteResolveStage.AskingMicrosoft => string.Format(Translator.AccountSetup_Discovery_AskingMicrosoft, update.Domain),
        AccountRouteResolveStage.CheckingSignIn => Translator.AccountSetup_Resolve_CheckingSignIn,
        AccountRouteResolveStage.LookingForSignInPage => Translator.AccountSetup_Resolve_LookingForSignInPage,
        AccountRouteResolveStage.LookingForExchange => Translator.AccountSetup_Resolve_LookingForExchange,
        _ => Translator.AccountSetup_Resolve_FindingServers
    };

    /// <summary>
    /// An empty account name becomes the provider's name; one that already exists gets the address appended.
    /// </summary>
    private async Task ApplyDerivedAccountNameAsync(AccountRouteResolution resolution)
    {
        if (!string.IsNullOrWhiteSpace(AccountName))
            return;

        var derived = resolution.Route switch
        {
            AccountRoute.Gmail => GetProviderName(MailProviderType.Gmail),
            AccountRoute.Outlook => GetProviderName(MailProviderType.Outlook),
            _ => string.IsNullOrWhiteSpace(resolution.ProviderDisplayName) ? resolution.Domain : resolution.ProviderDisplayName
        };

        if (string.IsNullOrWhiteSpace(derived))
            return;

        try
        {
            if (await _accountService.AccountNameExistsAsync(derived))
                derived = $"{derived} · {resolution.Address}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Checking the derived account name failed.");
        }

        AccountName = derived;
        _isAccountNameDerived = true;
    }

    private void ClearRoute()
    {
        _resolveVersion++;
        _resolution = null;
        _forcedChoice = null;
        IsResolveSlow = false;
        IsAskingMicrosoft = false;
        DiscoveryHelpText = string.Empty;
        ResetFeatureEntries();
        ResetImapSignIn();
        ExchangeUserName = string.Empty;
        NotifyRoutingLayoutChanged();
    }

    private string GetRouteEvidence()
    {
        var resolution = _resolution;
        if (resolution == null)
            return string.Empty;

        return resolution.Evidence switch
        {
            AccountRouteEvidence.Domain => string.Format(
                resolution.Route == AccountRoute.Gmail ? Translator.AccountSetup_Evidence_GoogleDomain : Translator.AccountSetup_Evidence_MicrosoftDomain,
                resolution.Domain),
            AccountRouteEvidence.Catalog => string.Format(Translator.AccountSetup_Evidence_Catalog,
                string.IsNullOrWhiteSpace(resolution.ProviderDisplayName) ? resolution.Domain : resolution.ProviderDisplayName),
            AccountRouteEvidence.Mx => resolution.Route == AccountRoute.Gmail
                ? Translator.AccountSetup_Evidence_GoogleMx
                : Translator.AccountSetup_Evidence_MicrosoftMx,
            AccountRouteEvidence.UserRealm => string.Format(Translator.AccountSetup_Evidence_UserRealm, resolution.Domain),
            AccountRouteEvidence.ImapHost => resolution.Route switch
            {
                AccountRoute.Gmail => Translator.AccountSetup_Evidence_GoogleImapHost,
                AccountRoute.Outlook => Translator.AccountSetup_Evidence_MicrosoftImapHost,
                _ => string.Format(Translator.AccountSetup_Evidence_ImapHost, ImapHostName)
            },
            AccountRouteEvidence.ExchangeAutodiscover => resolution.Route == AccountRoute.Exchange
                ? string.Format(Translator.AccountSetup_Evidence_ExchangeAutodiscover, ExchangeHostName)
                : string.Format(Translator.AccountSetup_Evidence_ExchangeOnline, resolution.Domain),
            _ => string.Empty
        };
    }

    private string ImapHostName => _resolution?.ImapHost
                                   ?? _resolution?.Imap?.IncomingHost
                                   ?? _resolution?.Domain
                                   ?? string.Empty;

    private string GetProviderName(MailProviderType providerType)
    {
        var name = _providerCatalog.GetAvailableProviders()
            .FirstOrDefault(provider => provider.Type == providerType)?.Name;

        return string.IsNullOrWhiteSpace(name) ? providerType.ToString() : name;
    }

    #region Gmail and Outlook

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithProviderCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInWithBrowserCommand))]
    public partial AccountSetupOAuthState OAuthState { get; set; }

    [ObservableProperty]
    public partial string SignedInAddress { get; set; } = string.Empty;

    public AccountSetupSignInKind SignInKind => Route switch
    {
        AccountRoute.Gmail => AccountSetupSignInKind.Google,
        AccountRoute.Outlook => AccountSetupSignInKind.Microsoft,
        AccountRoute.Imap => AccountSetupSignInKind.Imap,
        AccountRoute.Exchange => AccountSetupSignInKind.Exchange,
        _ => AccountSetupSignInKind.None
    };

    public bool IsNativeSignInVisible => IsSignInBlockVisible && IsNativeRoute;

    public string NativeSignInButtonText => Route switch
    {
        AccountRoute.Gmail => Translator.WinoAccount_SignInWithGoogle,
        AccountRoute.Exchange => Translator.ImapSetup_SignInWithOrganization,
        _ => Translator.WinoAccount_SignInWithMicrosoft
    };

    public bool IsOAuthWaiting => OAuthState == AccountSetupOAuthState.Waiting;
    public bool IsNativeSignInButtonVisible => IsNativeSignInVisible && OAuthState != AccountSetupOAuthState.SignedIn;

    /// <summary>
    /// Gmail and Outlook sign in from the To Do step's footer, in the primary button's place; the account is added as
    /// soon as that succeeds, so there is no separate sign-in step.
    /// </summary>
    public bool IsFooterSocialSignInVisible => Step == AccountSetupStep.Tasks && IsFooterSignInRoute;

    public string SignedInTitle => string.Format(Translator.ImapSetup_OAuthSignedInAs,
        string.IsNullOrWhiteSpace(SignedInAddress) ? EmailAddress?.Trim() ?? string.Empty : SignedInAddress);

    private bool CanSignInWithProvider() => IsFooterSignInRoute &&
                                            Phase is AccountSetupPhase.Features or AccountSetupPhase.SignIn &&
                                            OAuthState != AccountSetupOAuthState.Waiting;

    /// <summary>
    /// Signs the unsaved account in with Google or Microsoft, then reads the mailbox address the provider returned and
    /// refuses it when Wino already has it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignInWithProvider))]
    private async Task SignInWithProviderAsync()
    {
        if (IsExchangeRoute)
        {
            await SignInWithExchangeOrganizationAsync();
            return;
        }

        if (Phase == AccountSetupPhase.Features)
        {
            if (!ValidateFeatureSelection())
                return;

            BuildChecks();
        }

        var operation = StartOperation();
        HideFailure();
        SetSignInValidated(false);
        await DiscardNativeSignInAsync();

        var plan = BuildNativePlan(null);
        var transientAccount = _setupRunner.CreateTransientAccount(plan);
        _transientAccount = transientAccount;

        var accountCheck = Checks.FirstOrDefault(check => check.Kind == AccountSetupCheckKind.ProviderAccount);
        ResetCheck(accountCheck);

        Phase = AccountSetupPhase.SignIn;
        OAuthState = AccountSetupOAuthState.Waiting;

        try
        {
            var token = await _setupRunner.SignInTransientAccountAsync(transientAccount, operation.Token);
            if (operation.IsCancellationRequested)
                return;

            _nativeToken = token;
            OAuthState = AccountSetupOAuthState.SignedIn;
            Phase = AccountSetupPhase.Checking;

            if (accountCheck != null)
                accountCheck.State = ConnectionTestState.Testing;

            var address = await _setupRunner.ConfirmTransientAddressAsync(transientAccount, token, operation.Token);
            if (operation.IsCancellationRequested)
                return;

            SignedInAddress = address;

            if (await _accountService.AccountAddressExistsAsync(address))
            {
                var message = string.Format(Translator.AccountSetup_Failure_AddressExists, address);
                FailCheck(accountCheck, message);
                ShowFailure(Translator.DialogMessage_AccountExistsTitle, message, InfoBarMessageType.Error, AccountSetupRetryAction.None);
                await DiscardNativeSignInAsync();
                Phase = AccountSetupPhase.SignIn;
                return;
            }

            _confirmedAddress = address;

            if (accountCheck != null)
                accountCheck.State = ConnectionTestState.Succeeded;

            SetSignInValidated(true);
            Phase = AccountSetupPhase.SignIn;

            await AddAccountAsync();
        }
        catch (Exception ex) when (IsSignInCancellation(ex) && !operation.IsCancellationRequested)
        {
            await DiscardNativeSignInAsync();
            ShowFailure(Translator.AccountSetup_Failure_SignInTitle, Translator.AccountSetup_Failure_SignInCancelled, InfoBarMessageType.Warning, AccountSetupRetryAction.NativeSignIn);
            Phase = AccountSetupPhase.SignIn;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Signing in to {Route} during account setup failed.", Route);

            var message = string.Format(Translator.AccountSetup_Failure_SignIn, ex.Message);
            FailCheck(accountCheck, message);
            await DiscardNativeSignInAsync();
            ShowFailure(Translator.AccountSetup_Failure_SignInTitle, message, InfoBarMessageType.Error, AccountSetupRetryAction.NativeSignIn);
            Phase = AccountSetupPhase.SignIn;
        }
    }

    private static bool IsSignInCancellation(Exception exception)
        => exception is AccountSetupCanceledException ||
           exception.Message.Contains(nameof(AccountSetupCanceledException), StringComparison.Ordinal) ||
           exception is OperationCanceledException;

    private AccountSetupPlan BuildNativePlan(NativeSignInResult? signIn) => new()
    {
        Kind = AccountSetupKind.Native,
        ProviderType = RouteProviderType,
        SpecialImapProvider = SpecialImapProvider.None,
        ProviderName = NativeProviderName,
        AccountName = AccountName?.Trim(),
        AccountColorHex = SelectedColor?.Hex ?? string.Empty,
        InitialSynchronizationRange = SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths,
        IsMailAccessEnabled = Capabilities.IsMailEnabled,
        IsCalendarAccessEnabled = Capabilities.IsCalendarEnabled,
        IsContactAccessEnabled = Capabilities.IsContactEnabled,
        IsTaskAccessEnabled = Capabilities.IsTaskEnabled,
        CalendarIntegrationSource = ToIntegrationSource(Capabilities.CalendarMode),
        ContactIntegrationSource = ToIntegrationSource(Capabilities.ContactMode),
        TaskIntegrationSource = ToIntegrationSource(Capabilities.TaskMode),
        EmailAddress = EmailAddress?.Trim(),
        NativeSignIn = signIn
    };

    /// <summary>Forgets a Gmail or Outlook sign-in that will not be saved: the Gmail token file under the transient id goes.</summary>
    private async Task DiscardNativeSignInAsync()
    {
        var transientAccount = _transientAccount;
        _transientAccount = null;
        _nativeToken = null;
        _confirmedAddress = null;
        SignedInAddress = string.Empty;

        if (OAuthState != AccountSetupOAuthState.Idle && IsNativeRoute)
            OAuthState = AccountSetupOAuthState.Idle;

        if (transientAccount == null)
            return;

        try
        {
            await _setupRunner.DiscardTransientSignInAsync(transientAccount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discarding an abandoned setup sign-in failed.");
        }
    }

    #endregion

    partial void OnOAuthStateChanged(AccountSetupOAuthState value) => NotifySignInLayoutChanged();

    partial void OnSignedInAddressChanged(string value)
    {
        OnPropertyChanged(nameof(SignedInTitle));
        NotifyConnectionStatusChanged();
    }

    private void NotifyRoutingLayoutChanged()
    {
        OnPropertyChanged(nameof(Route));
        OnPropertyChanged(nameof(IsResolvingVisible));
        OnPropertyChanged(nameof(IsNotFoundVisible));
        OnPropertyChanged(nameof(NotFoundMessage));
        OnPropertyChanged(nameof(RouteProviderType));
        OnPropertyChanged(nameof(RouteSpecialImapProvider));
        OnPropertyChanged(nameof(RouteEvidenceText));
        OnPropertyChanged(nameof(RouteShortTitle));
        OnPropertyChanged(nameof(HasRouteShortTitle));
        OnPropertyChanged(nameof(DiscoveryText));
        OnPropertyChanged(nameof(AskingMicrosoftText));
        OnPropertyChanged(nameof(SignInKind));
        OnPropertyChanged(nameof(NativeSignInButtonText));
        NotifyProviderOverrideChanged();
    }
}
