#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The on-premises Exchange route. The capability probe that ran during resolution decides the sign-in: a server that
/// names an authorization server signs in with the organization's page from the To Do step's footer and is added right
/// after, like Gmail and Outlook; any other server gets a user name and password on the sign-in step.
/// </summary>
public partial class AccountSetupPageViewModel
{
    /// <summary>The unsaved account a modern-auth sign-in stored its refresh token under.</summary>
    private MailAccount? _exchangeTransientAccount;

    /// <summary>The Exchange sign-in name when it differs from the address: DOMAIN\user or a UPN. Empty means the address.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string ExchangeUserName { get; set; } = string.Empty;

    public bool IsExchangeRoute => Route == AccountRoute.Exchange;

    /// <summary>The server names an authorization server and this device can show its sign-in page.</summary>
    public bool IsExchangeModernAuth => IsExchangeRoute &&
                                        _resolution?.Exchange?.IsModernAuth == true &&
                                        _exchangeSignInService?.IsSupported == true;

    public bool IsExchangePasswordVisible => IsSignInBlockVisible && IsExchangeRoute && !IsExchangeModernAuth;

    public string ExchangeUserNamePlaceholder => EmailAddress?.Trim() ?? string.Empty;

    public string ExchangePasswordCaption => string.Format(Translator.ExchangeSetup_PasswordCaption, ExchangeHostName);

    /// <summary>Gmail, Outlook and modern-auth Exchange sign in from the To Do step's footer instead of a sign-in step.</summary>
    private bool IsFooterSignInRoute => IsNativeRoute || IsExchangeModernAuth;

    private static string ExchangeProviderName => Translator.ProviderDetail_Exchange_Title;

    private string ExchangeHostName => Uri.TryCreate(_resolution?.Exchange?.EwsUrl, UriKind.Absolute, out var uri)
        ? uri.Host
        : _resolution?.Domain ?? AddressDomain;

    private string ExchangeAuthorityHost => Uri.TryCreate(_resolution?.Exchange?.AuthProbe.Authority, UriKind.Absolute, out var uri)
        ? uri.Host
        : ExchangeHostName;

    private bool CanCheckExchangePassword() => Phase == AccountSetupPhase.SignIn &&
                                               IsExchangePasswordVisible &&
                                               !string.IsNullOrEmpty(Password);

    /// <summary>Checks the typed user name and password against the server; Add account follows once it passes.</summary>
    private async Task CheckExchangePasswordAsync()
    {
        var operation = StartOperation();
        HideFailure();
        ResetChecks();
        _pendingServer = null;
        SetSignInValidated(false);

        var check = Checks.FirstOrDefault(candidate => candidate.Kind == AccountSetupCheckKind.ProviderAccount);
        Phase = AccountSetupPhase.Checking;

        if (check != null)
            check.State = ConnectionTestState.Testing;

        CustomServerInformation server;
        try
        {
            server = BuildExchangeServerInformation();
        }
        catch (Exception ex)
        {
            FailCheck(check, ex.Message);
            ShowFailure(Translator.AccountSetup_Failure_Title, ex.Message, InfoBarMessageType.Error, AccountSetupRetryAction.None);
            Phase = AccountSetupPhase.SignIn;
            return;
        }

        var userName = ExchangeUserName?.Trim();
        server.IncomingServerUsername = string.IsNullOrEmpty(userName) ? EmailAddress?.Trim() ?? string.Empty : userName;
        server.IncomingServerPassword = Password;

        var result = await TestExchangeConnectionAsync(CreateExchangeAccount(server, Guid.NewGuid()), operation.Token);
        if (operation.IsCancellationRequested || result == null)
            return;

        if (!result.IsSuccess)
        {
            var message = DescribeExchangeFailure(result);
            FailCheck(check, message);
            ShowFailure(Translator.AccountSetup_Failure_Title, message, InfoBarMessageType.Error, AccountSetupRetryAction.ExchangePasswordCheck);
            Phase = AccountSetupPhase.SignIn;
            return;
        }

        ApplyDetectedTransport(server, result);

        if (check != null)
            check.State = ConnectionTestState.Succeeded;

        _pendingServer = server;
        SetSignInValidated(true);
        Phase = AccountSetupPhase.SignIn;
    }

    /// <summary>
    /// Signs in on the organization's page in the embedded browser, checks the mailbox with the new token, and adds the
    /// account at once.
    /// </summary>
    private async Task SignInWithExchangeOrganizationAsync()
    {
        var signInService = _exchangeSignInService;
        var authority = _resolution?.Exchange?.AuthProbe.Authority;
        if (signInService == null || string.IsNullOrWhiteSpace(authority))
            return;

        if (Phase == AccountSetupPhase.Features)
        {
            if (!ValidateFeatureSelection())
                return;

            BuildChecks();
        }

        var operation = StartOperation();
        HideFailure();
        SetSignInValidated(false);
        _pendingServer = null;
        await DiscardExchangeSignInAsync();

        CustomServerInformation server;
        try
        {
            server = BuildExchangeServerInformation();
        }
        catch (Exception ex)
        {
            ShowFailure(Translator.AccountSetup_Failure_SignInTitle, ex.Message, InfoBarMessageType.Error, AccountSetupRetryAction.None);
            return;
        }

        ExchangeOAuthDefaults.Apply(server, authority);
        server.IncomingServerUsername = EmailAddress?.Trim() ?? string.Empty;

        var transientAccount = CreateExchangeAccount(server, Guid.NewGuid());
        _exchangeTransientAccount = transientAccount;

        var check = Checks.FirstOrDefault(candidate => candidate.Kind == AccountSetupCheckKind.ProviderAccount);
        ResetCheck(check);

        Phase = AccountSetupPhase.SignIn;
        OAuthState = AccountSetupOAuthState.Waiting;

        try
        {
            var identity = await signInService.SignInAsync(transientAccount.Id, server, EmailAddress?.Trim(), operation.Token);
            if (operation.IsCancellationRequested)
                return;

            OAuthState = AccountSetupOAuthState.SignedIn;
            Phase = AccountSetupPhase.Checking;

            if (check != null)
                check.State = ConnectionTestState.Testing;

            if (string.IsNullOrWhiteSpace(SenderName) && !string.IsNullOrWhiteSpace(identity.DisplayName))
                SenderName = identity.DisplayName;

            var result = await TestExchangeConnectionAsync(transientAccount, operation.Token);
            if (operation.IsCancellationRequested || result == null)
                return;

            if (!result.IsSuccess)
            {
                var message = DescribeExchangeFailure(result);
                FailCheck(check, message);
                await DiscardExchangeSignInAsync();
                ShowFailure(Translator.AccountSetup_Failure_Title, message, InfoBarMessageType.Error, AccountSetupRetryAction.NativeSignIn);
                Phase = AccountSetupPhase.SignIn;
                return;
            }

            ApplyDetectedTransport(server, result);

            if (check != null)
                check.State = ConnectionTestState.Succeeded;

            _pendingServer = server;
            SetSignInValidated(true);
            Phase = AccountSetupPhase.SignIn;

            await AddAccountAsync();
        }
        catch (Exception ex) when (IsSignInCancellation(ex) && !operation.IsCancellationRequested)
        {
            await DiscardExchangeSignInAsync();
            ShowFailure(Translator.AccountSetup_Failure_SignInTitle, Translator.AccountSetup_Failure_SignInCancelled, InfoBarMessageType.Warning, AccountSetupRetryAction.NativeSignIn);
            Phase = AccountSetupPhase.SignIn;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (ExchangeSignInException ex)
        {
            Log.Warning(ex, "Exchange sign-in during account setup failed ({FailureKind}).", ex.Kind);
            FailCheck(check, ex.Message);
            await DiscardExchangeSignInAsync();
            ShowFailure(Translator.AccountSetup_Failure_SignInTitle, ex.Message, InfoBarMessageType.Error, AccountSetupRetryAction.NativeSignIn);
            Phase = AccountSetupPhase.SignIn;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exchange sign-in during account setup failed.");

            var message = string.Format(Translator.AccountSetup_Failure_SignIn, ex.Message);
            FailCheck(check, message);
            await DiscardExchangeSignInAsync();
            ShowFailure(Translator.AccountSetup_Failure_SignInTitle, message, InfoBarMessageType.Error, AccountSetupRetryAction.NativeSignIn);
            Phase = AccountSetupPhase.SignIn;
        }
    }

    /// <summary>The server information both sign-ins start from: the discovered EWS URL and the calendar mode.</summary>
    private CustomServerInformation BuildExchangeServerInformation()
    {
        var ewsUrl = _resolution?.Exchange?.EwsUrl;
        if (string.IsNullOrWhiteSpace(ewsUrl))
            throw new InvalidOperationException(Translator.ExchangeSetup_ServerAddressInvalid);

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            Address = EmailAddress?.Trim() ?? string.Empty,
            IncomingServer = ewsUrl,
            IncomingServerType = CustomIncomingServerType.Exchange,
            ExchangeTransport = ExchangeTransport.Automatic,
            CalendarSupportMode = Capabilities.CalendarMode == AccountCapabilityMode.Local
                ? ImapCalendarSupportMode.LocalOnly
                : ImapCalendarSupportMode.Disabled,
            MaxConcurrentClients = 5
        };
    }

    /// <summary>An account that is never saved: the authenticators read its server information and key tokens by its id.</summary>
    private MailAccount CreateExchangeAccount(CustomServerInformation server, Guid id) => new()
    {
        Id = id,
        Name = AccountName?.Trim() ?? string.Empty,
        Address = EmailAddress?.Trim() ?? string.Empty,
        ProviderType = MailProviderType.Exchange,
        ServerInformation = server
    };

    private async Task<ExchangeConnectionTestResult?> TestExchangeConnectionAsync(MailAccount account, CancellationToken cancellationToken)
    {
        if (_exchangeConnectionTester == null)
            return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.Succeeded);

        try
        {
            return await _exchangeConnectionTester.TestAsync(account, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Under Automatic, setup records what Autodiscover said so the first synchronizer picks the right transport.</summary>
    private static void ApplyDetectedTransport(CustomServerInformation server, ExchangeConnectionTestResult result)
        => server.DetectedExchangeTransport = server.ExchangeTransport == ExchangeTransport.Automatic
            ? result.DetectedTransport
            : ExchangeTransport.Automatic;

    private string DescribeExchangeFailure(ExchangeConnectionTestResult result)
    {
        var message = result.Status == ExchangeConnectionTestStatus.CredentialRejected
            ? string.Format(Translator.AccountSetup_Failure_PasswordRejected, ExchangeHostName)
            : string.Format(Translator.AccountSetup_Failure_Connection, ExchangeHostName, result.FailureReason ?? string.Empty);

        return string.Format(Translator.AccountSetup_Failure_Row, ExchangeProviderName, message);
    }

    private AccountSetupPlan BuildExchangePlan(string accountName, string colorHex, InitialSynchronizationRange range)
    {
        var server = _pendingServer ?? throw new InvalidOperationException(Translator.AccountSetup_Failure_Title);

        return new AccountSetupPlan
        {
            Kind = AccountSetupKind.Exchange,
            ProviderType = MailProviderType.Exchange,
            SpecialImapProvider = SpecialImapProvider.None,
            ProviderName = ExchangeProviderName,
            AccountName = accountName,
            AccountColorHex = colorHex,
            InitialSynchronizationRange = range,
            IsMailAccessEnabled = Capabilities.IsMailEnabled,
            IsCalendarAccessEnabled = Capabilities.IsCalendarEnabled,
            IsContactAccessEnabled = Capabilities.IsContactEnabled,
            IsTaskAccessEnabled = Capabilities.IsTaskEnabled,
            CalendarIntegrationSource = ToIntegrationSource(Capabilities.CalendarMode),
            ContactIntegrationSource = ToIntegrationSource(Capabilities.ContactMode),
            TaskIntegrationSource = ToIntegrationSource(Capabilities.TaskMode),
            CalendarSupportMode = server.CalendarSupportMode,
            EmailAddress = EmailAddress?.Trim(),
            DisplayName = EffectiveSenderName,
            Exchange = new ExchangeAccountSetupData
            {
                DisplayName = EffectiveSenderName,
                EmailAddress = EmailAddress?.Trim(),
                ServerInformation = server,
                SignedInAccountId = server.UseOAuthAuthentication ? _exchangeTransientAccount?.Id : null
            }
        };
    }

    /// <summary>Forgets a modern-auth Exchange sign-in that will not be saved, with the refresh token it stored.</summary>
    private async Task DiscardExchangeSignInAsync()
    {
        var transientAccount = _exchangeTransientAccount;
        _exchangeTransientAccount = null;

        if (OAuthState != AccountSetupOAuthState.Idle && IsExchangeRoute)
            OAuthState = AccountSetupOAuthState.Idle;

        if (transientAccount == null)
            return;

        try
        {
            await _setupRunner.DiscardTransientSignInAsync(transientAccount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discarding an abandoned Exchange sign-in failed.");
        }
    }

    /// <summary>
    /// Opens the Exchange settings page with what this page found. It saves through the wizard context and the progress
    /// page like the IMAP Advanced setup.
    /// </summary>
    private async Task OpenExchangeAdvancedSetupAsync()
    {
        var address = EmailAddress?.Trim() ?? string.Empty;
        var hasFeatures = Phase is not (AccountSetupPhase.Identity or AccountSetupPhase.Resolving or AccountSetupPhase.NotFound);
        var calendarMode = hasFeatures ? Capabilities.CalendarMode : AccountCapabilityMode.Provider;
        var contactMode = hasFeatures ? Capabilities.ContactMode : AccountCapabilityMode.Provider;
        var taskMode = hasFeatures ? Capabilities.TaskMode : AccountCapabilityMode.Provider;

        var context = new ExchangeSettingsNavigationContext
        {
            EmailAddress = address,
            DisplayName = EffectiveSenderName,
            EwsUrl = _resolution?.Exchange?.EwsUrl,
            UserName = ExchangeUserName?.Trim()
        };

        WizardContext.Reset();
        WizardContext.SelectedProvider = new ProviderDetail(MailProviderType.Exchange, SpecialImapProvider.None);
        WizardContext.AccountName = GetAdvancedAccountName(address);
        WizardContext.AccountColorHex = SelectedColor?.Hex ?? string.Empty;
        WizardContext.SelectedInitialSynchronizationRange = SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths;
        WizardContext.IsMailAccessEnabled = !hasFeatures || Capabilities.IsMailEnabled;
        WizardContext.IsCalendarAccessEnabled = calendarMode != AccountCapabilityMode.Off;
        WizardContext.IsContactAccessEnabled = contactMode != AccountCapabilityMode.Off;
        WizardContext.IsTaskAccessEnabled = taskMode != AccountCapabilityMode.Off;
        WizardContext.CalendarIntegrationSource = calendarMode == AccountCapabilityMode.Provider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Local;
        WizardContext.ContactIntegrationSource = contactMode == AccountCapabilityMode.Provider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Local;
        WizardContext.TaskIntegrationSource = taskMode == AccountCapabilityMode.Provider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Local;
        WizardContext.CalendarSupportMode = calendarMode == AccountCapabilityMode.Local ? ImapCalendarSupportMode.LocalOnly : ImapCalendarSupportMode.Disabled;
        WizardContext.EmailAddress = address;
        WizardContext.DisplayName = EffectiveSenderName;

        await DiscardSignInAsync();
        ClearRoute();
        Phase = AccountSetupPhase.Identity;

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.ExchangeSetup_Title,
            WinoPage.ExchangeSettingsPage,
            context));
    }

    partial void OnExchangeUserNameChanged(string value) => InvalidateCheckedCredential();

    private void NotifyExchangeSignInLayoutChanged()
    {
        OnPropertyChanged(nameof(IsExchangeRoute));
        OnPropertyChanged(nameof(IsExchangeModernAuth));
        OnPropertyChanged(nameof(IsExchangePasswordVisible));
        OnPropertyChanged(nameof(ExchangeUserNamePlaceholder));
        OnPropertyChanged(nameof(ExchangePasswordCaption));
    }
}
