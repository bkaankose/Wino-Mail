#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>One connection method in the Exchange settings.</summary>
public sealed class ExchangeTransportOption(ExchangeTransport transport, string title)
{
    public ExchangeTransport Transport { get; } = transport;
    public string Title { get; } = title;

    public override string ToString() => Title;

    /// <summary>Automatic, MAPI over HTTP and EWS, in the order of <see cref="ExchangeTransport"/>.</summary>
    public static List<ExchangeTransportOption> CreateAll() =>
    [
        new(ExchangeTransport.Automatic, Translator.ExchangeSetup_TransportAutomatic),
        new(ExchangeTransport.MapiHttp, Translator.ExchangeSetup_TransportMapi),
        new(ExchangeTransport.Ews, Translator.ExchangeSetup_TransportEws)
    ];
}

/// <summary>
/// Advanced setup for an on-premises Exchange account: the address, the server (found by Autodiscover when left empty)
/// and the connection method, then the sign-in the server asks for. A password is checked before the account is added;
/// a modern-auth sign-in adds the account as soon as it succeeds. It saves through the wizard context and the progress
/// page like the IMAP Advanced setup.
/// </summary>
public partial class ExchangeSettingsPageViewModel : MailBaseViewModel
{
    private const string DefaultEwsPath = "/EWS/Exchange.asmx";

    private readonly WelcomeWizardContext _wizardContext;
    private readonly IExchangeAutoDiscoveryService _autoDiscoveryService;
    private readonly IExchangeAuthCapabilityProbe _authCapabilityProbe;
    private readonly IExchangeConnectionTester _connectionTester;
    private readonly IAccountService _accountService;
    private readonly IAccountSetupRunner _setupRunner;
    private readonly IExchangeInteractiveSignInService? _signInService;

    private CancellationTokenSource? _operation;
    private string? _authority;

    /// <summary>The unsaved account a modern-auth sign-in stored its refresh token under, until setup takes it.</summary>
    private MailAccount? _signedInAccount;
    private bool _isHandedOff;

    public List<ExchangeTransportOption> TransportOptions { get; } = ExchangeTransportOption.CreateAll();

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string EmailAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ServerAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ExchangeTransportOption? SelectedTransport { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>The server was found and probed; the sign-in it asks for is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerStageVisible))]
    [NotifyPropertyChangedFor(nameof(IsPasswordStageVisible))]
    [NotifyPropertyChangedFor(nameof(IsModernAuthStageVisible))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryVisible))]
    [NotifyPropertyChangedFor(nameof(PrimaryText))]
    [NotifyPropertyChangedFor(nameof(ServerSummary))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial bool IsSignInStage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordStageVisible))]
    [NotifyPropertyChangedFor(nameof(IsModernAuthStageVisible))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryVisible))]
    [NotifyPropertyChangedFor(nameof(PrimaryText))]
    [NotifyPropertyChangedFor(nameof(SignInDescription))]
    public partial bool IsModernAuth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInWithOrganizationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeServerCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFailureOpen { get; set; }

    [ObservableProperty]
    public partial string FailureMessage { get; set; } = string.Empty;

    public bool IsIdle => !IsBusy;
    public bool IsServerStageVisible => !IsSignInStage;
    public bool IsPasswordStageVisible => IsSignInStage && !IsModernAuth;
    public bool IsModernAuthStageVisible => IsSignInStage && IsModernAuth;
    public bool IsPrimaryVisible => !IsModernAuthStageVisible;

    public string PrimaryText => IsSignInStage ? Translator.ProviderSelection_AddAccountButton : Translator.ProviderSelection_ContinueButton;

    public string ServerSummary => $"{EmailAddress?.Trim()} · {ServerHost}";

    public string SignInDescription => string.Format(
        IsModernAuth ? Translator.ExchangeSetup_ModernAuthCaption : Translator.ExchangeSetup_PasswordCaption,
        ServerHost);

    public string UserNamePlaceholder => EmailAddress?.Trim() ?? string.Empty;

    private string ServerHost => Uri.TryCreate(ServerAddress?.Trim(), UriKind.Absolute, out var uri) ? uri.Host : ServerAddress?.Trim() ?? string.Empty;

    public ExchangeSettingsPageViewModel(
        WelcomeWizardContext wizardContext,
        IExchangeAutoDiscoveryService autoDiscoveryService,
        IExchangeAuthCapabilityProbe authCapabilityProbe,
        IExchangeConnectionTester connectionTester,
        IAccountService accountService,
        IAccountSetupRunner setupRunner,
        IExchangeInteractiveSignInService? signInService = null)
    {
        _wizardContext = wizardContext;
        _autoDiscoveryService = autoDiscoveryService;
        _authCapabilityProbe = authCapabilityProbe;
        _connectionTester = connectionTester;
        _accountService = accountService;
        _setupRunner = setupRunner;
        _signInService = signInService;

        SelectedTransport = TransportOptions[0];
    }

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (mode == NavigationMode.Back)
        {
            _isHandedOff = false;
            await ForgetSavedSignInAsync();
            return;
        }

        if (parameters is ExchangeSettingsNavigationContext context)
        {
            EmailAddress = context.EmailAddress;
            DisplayName = context.DisplayName;
            ServerAddress = context.EwsUrl ?? string.Empty;
            UserName = context.UserName ?? string.Empty;
        }
        else
        {
            EmailAddress = _wizardContext.EmailAddress ?? string.Empty;
            DisplayName = _wizardContext.DisplayName ?? string.Empty;
        }

        PrepareWizardContext();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        CancelOperation();

        if (!_isHandedOff)
            _ = DiscardSignInAsync();

        base.OnNavigatedFrom(mode, parameters);
    }

    private bool CanExecutePrimary() => !IsBusy &&
                                        (IsSignInStage ? !IsModernAuth && !string.IsNullOrEmpty(Password) : !string.IsNullOrWhiteSpace(EmailAddress));

    [RelayCommand(CanExecute = nameof(CanExecutePrimary))]
    private Task PrimaryAsync() => IsSignInStage ? CheckPasswordAndContinueAsync() : FindServerAsync();

    private bool CanSignInWithOrganization() => !IsBusy && IsModernAuthStageVisible && _signInService?.IsSupported == true;

    [RelayCommand(CanExecute = nameof(CanSignInWithOrganization))]
    private async Task SignInWithOrganizationAsync()
    {
        var signInService = _signInService;
        var authority = _authority;
        if (signInService == null || string.IsNullOrWhiteSpace(authority))
            return;

        var operation = StartOperation();
        HideFailure();
        await DiscardSignInAsync();

        var server = BuildServerInformation();
        ExchangeOAuthDefaults.Apply(server, authority);
        server.IncomingServerUsername = EmailAddress.Trim();

        var account = CreateAccount(server);
        _signedInAccount = account;

        IsBusy = true;
        BusyText = Translator.ExchangeSetup_SigningIn;

        try
        {
            var identity = await signInService.SignInAsync(account.Id, server, EmailAddress.Trim(), operation.Token);
            if (operation.IsCancellationRequested)
                return;

            if (string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(identity.DisplayName))
                DisplayName = identity.DisplayName;

            BusyText = Translator.ExchangeSetup_Checking;
            var result = await _connectionTester.TestAsync(account, operation.Token);
            if (operation.IsCancellationRequested)
                return;

            if (!result.IsSuccess)
            {
                await DiscardSignInAsync();
                ShowFailure(DescribeFailure(result));
                return;
            }

            ApplyDetectedTransport(server, result);
            Continue(server, account.Id);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (AccountSetupCanceledException)
        {
            await DiscardSignInAsync();
            ShowFailure(Translator.AccountSetup_Failure_SignInCancelled);
        }
        catch (ExchangeSignInException ex)
        {
            await DiscardSignInAsync();
            ShowFailure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exchange sign-in from Exchange setup failed.");
            await DiscardSignInAsync();
            ShowFailure(string.Format(Translator.AccountSetup_Failure_SignIn, ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
                IsBusy = false;
        }
    }

    private bool CanChangeServer() => !IsBusy && IsSignInStage;

    [RelayCommand(CanExecute = nameof(CanChangeServer))]
    private async Task ChangeServerAsync()
    {
        CancelOperation();
        HideFailure();
        await DiscardSignInAsync();

        Password = string.Empty;
        IsSignInStage = false;
    }

    [RelayCommand]
    private void Back()
    {
        CancelOperation();
        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    [RelayCommand]
    private void HideFailure()
    {
        IsFailureOpen = false;
        FailureMessage = string.Empty;
    }

    /// <summary>Finds the server by Autodiscover when none was typed, then asks it how it accepts sign-in.</summary>
    private async Task FindServerAsync()
    {
        HideFailure();

        var address = EmailAddress?.Trim() ?? string.Empty;
        if (!MailAccountAddressValidator.IsValid(address))
        {
            ShowFailure(Translator.AccountSetup_EmailInvalid);
            return;
        }

        var operation = StartOperation();
        IsBusy = true;
        BusyText = Translator.ExchangeSetup_Finding;

        try
        {
            var serverAddress = ServerAddress?.Trim() ?? string.Empty;
            if (serverAddress.Length == 0)
            {
                var discovered = await _autoDiscoveryService.TryDiscoverEwsUrlAsync(address, operation.Token);
                if (operation.IsCancellationRequested)
                    return;

                if (string.IsNullOrWhiteSpace(discovered))
                {
                    MailAccountAddressValidator.TryGetDomain(address, out var domain);
                    ShowFailure(string.Format(Translator.ExchangeSetup_NotFound, domain ?? address));
                    return;
                }

                serverAddress = discovered;
            }

            var ewsUrl = NormalizeServerAddress(serverAddress);
            if (ewsUrl == null)
            {
                ShowFailure(Translator.ExchangeSetup_ServerAddressInvalid);
                return;
            }

            if (KnownMailHosts.IsExchangeOnlineHost(ewsUrl.Host))
            {
                ShowFailure(string.Format(Translator.ExchangeSetup_Microsoft365, address));
                return;
            }

            ServerAddress = ewsUrl.AbsoluteUri;

            var probe = await _authCapabilityProbe.ProbeAsync(ewsUrl.AbsoluteUri, address, operation.Token);
            if (operation.IsCancellationRequested)
                return;

            _authority = probe.Capability == ExchangeAuthCapability.ModernAuthAvailable ? probe.Authority : null;
            IsModernAuth = !string.IsNullOrWhiteSpace(_authority) && _signInService?.IsSupported == true;
            IsSignInStage = true;
            OnPropertyChanged(nameof(UserNamePlaceholder));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Finding the Exchange server failed.");
            ShowFailure(string.Format(Translator.AccountSetup_Failure_Connection, ServerHost, ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
                IsBusy = false;

            SignInWithOrganizationCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task CheckPasswordAndContinueAsync()
    {
        HideFailure();

        var operation = StartOperation();
        IsBusy = true;
        BusyText = Translator.ExchangeSetup_Checking;

        try
        {
            var server = BuildServerInformation();
            var userName = UserName?.Trim();
            server.IncomingServerUsername = string.IsNullOrEmpty(userName) ? EmailAddress.Trim() : userName;
            server.IncomingServerPassword = Password;

            var result = await _connectionTester.TestAsync(CreateAccount(server), operation.Token);
            if (operation.IsCancellationRequested)
                return;

            if (!result.IsSuccess)
            {
                ShowFailure(DescribeFailure(result));
                return;
            }

            ApplyDetectedTransport(server, result);
            Continue(server, signedInAccountId: null);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
                IsBusy = false;
        }
    }

    private void Continue(CustomServerInformation server, Guid? signedInAccountId)
    {
        _wizardContext.EmailAddress = EmailAddress.Trim();
        _wizardContext.DisplayName = DisplayName?.Trim() ?? string.Empty;
        _wizardContext.ExchangeSetup = new ExchangeAccountSetupData
        {
            DisplayName = DisplayName?.Trim(),
            EmailAddress = EmailAddress.Trim(),
            ServerInformation = server,
            SignedInAccountId = signedInAccountId
        };

        _isHandedOff = true;
        Messenger.Send(new BreadcrumbNavigationRequested(Translator.WelcomeWizard_Step3Title, WinoPage.AccountSetupProgressPage));
    }

    /// <summary>The wizard context carries the modes; Exchange serves every synced mode itself.</summary>
    private void PrepareWizardContext()
    {
        _wizardContext.SelectedProvider = new ProviderDetail(MailProviderType.Exchange, SpecialImapProvider.None);

        if (_wizardContext.CalendarIntegrationSource == AccountIntegrationSource.Dav)
            _wizardContext.CalendarIntegrationSource = AccountIntegrationSource.Provider;

        if (_wizardContext.ContactIntegrationSource == AccountIntegrationSource.Dav)
            _wizardContext.ContactIntegrationSource = AccountIntegrationSource.Provider;

        if (string.IsNullOrWhiteSpace(_wizardContext.AccountName) &&
            MailAccountAddressValidator.TryGetDomain(EmailAddress?.Trim() ?? string.Empty, out var domain))
        {
            _wizardContext.AccountName = domain;
        }
    }

    private CustomServerInformation BuildServerInformation() => new()
    {
        Id = Guid.NewGuid(),
        Address = EmailAddress.Trim(),
        IncomingServer = ServerAddress.Trim(),
        IncomingServerType = CustomIncomingServerType.Exchange,
        ExchangeTransport = SelectedTransport?.Transport ?? ExchangeTransport.Automatic,
        CalendarSupportMode = _wizardContext.IsCalendarAccessEnabled && _wizardContext.CalendarIntegrationSource == AccountIntegrationSource.Local
            ? ImapCalendarSupportMode.LocalOnly
            : ImapCalendarSupportMode.Disabled,
        MaxConcurrentClients = 5
    };

    private MailAccount CreateAccount(CustomServerInformation server) => new()
    {
        Id = Guid.NewGuid(),
        Name = _wizardContext.AccountName ?? string.Empty,
        Address = EmailAddress.Trim(),
        ProviderType = MailProviderType.Exchange,
        ServerInformation = server
    };

    private static void ApplyDetectedTransport(CustomServerInformation server, ExchangeConnectionTestResult result)
        => server.DetectedExchangeTransport = server.ExchangeTransport == ExchangeTransport.Automatic
            ? result.DetectedTransport
            : ExchangeTransport.Automatic;

    private string DescribeFailure(ExchangeConnectionTestResult result)
        => result.Status == ExchangeConnectionTestStatus.CredentialRejected
            ? string.Format(Translator.AccountSetup_Failure_PasswordRejected, ServerHost)
            : string.Format(Translator.AccountSetup_Failure_Connection, ServerHost, result.FailureReason ?? string.Empty);

    /// <summary>
    /// A bare host becomes its default EWS endpoint; only HTTPS is accepted because the credential is sent to it.
    /// </summary>
    private static Uri? NormalizeServerAddress(string value)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            return null;

        return uri.AbsolutePath is "" or "/"
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + DefaultEwsPath)
            : uri;
    }

    private void ShowFailure(string message)
    {
        FailureMessage = message;
        IsFailureOpen = true;
    }

    /// <summary>After the progress page, a sign-in whose account now exists belongs to it; one that was not saved is discarded.</summary>
    private async Task ForgetSavedSignInAsync()
    {
        var account = _signedInAccount;
        if (account == null)
            return;

        try
        {
            if (await _accountService.GetAccountAsync(account.Id) != null)
            {
                _signedInAccount = null;
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Checking the account of an Exchange sign-in failed.");
            _signedInAccount = null;
            return;
        }

        await DiscardSignInAsync();
    }

    private async Task DiscardSignInAsync()
    {
        var account = _signedInAccount;
        _signedInAccount = null;

        if (account == null)
            return;

        try
        {
            await _setupRunner.DiscardTransientSignInAsync(account);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discarding an abandoned Exchange sign-in failed.");
        }
    }

    private CancellationTokenSource StartOperation()
    {
        CancelOperation();

        var operation = new CancellationTokenSource();
        _operation = operation;
        return operation;
    }

    private void CancelOperation()
    {
        var operation = _operation;
        _operation = null;

        try
        {
            operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        IsBusy = false;
    }

    partial void OnEmailAddressChanged(string value) => OnPropertyChanged(nameof(UserNamePlaceholder));
}
