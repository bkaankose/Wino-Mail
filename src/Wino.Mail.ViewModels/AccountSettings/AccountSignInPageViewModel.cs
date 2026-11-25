#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Common;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>
/// Sign-in and servers of an account whose provider serves every app (Outlook, Gmail, Exchange): the sign-in, and for
/// Exchange its connection method and password. IMAP and POP3 accounts use the server page instead.
/// </summary>
public partial class AccountSignInPageViewModel : CoreBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IMailProviderRegistry _providers;
    private readonly IExchangeConnectionTester? _exchangeConnectionTester;
    private readonly IAccountReauthenticationService? _reauthenticationService;
    private readonly IExchangeInteractiveSignInService? _exchangeSignInService;
    private bool _isLoading;

    public AccountSignInPageViewModel(
        IMessenger messenger,
        IMailDialogService dialogService,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager,
        IMailProviderRegistry providers,
        IExchangeConnectionTester? exchangeConnectionTester = null,
        IAccountReauthenticationService? reauthenticationService = null,
        IExchangeInteractiveSignInService? exchangeSignInService = null) : base(messenger)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _synchronizationManager = synchronizationManager;
        _providers = providers;
        _exchangeConnectionTester = exchangeConnectionTester;
        _reauthenticationService = reauthenticationService;
        _exchangeSignInService = exchangeSignInService;
    }

    public List<ExchangeTransportOption> ExchangeTransportOptions { get; } = ExchangeTransportOption.CreateAll();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Address))]
    [NotifyPropertyChangedFor(nameof(IsExchangeAccount))]
    [NotifyPropertyChangedFor(nameof(IsOAuthAccount))]
    [NotifyPropertyChangedFor(nameof(SignedInTitle))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(IsSignInAgainVisible))]
    public partial MailAccount? Account { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExchangeModernAuth))]
    [NotifyPropertyChangedFor(nameof(IsExchangePasswordAuth))]
    [NotifyPropertyChangedFor(nameof(IsSignInAgainVisible))]
    [NotifyPropertyChangedFor(nameof(SignedInTitle))]
    [NotifyPropertyChangedFor(nameof(ExchangeServerDescription))]
    public partial CustomServerInformation? ServerInformation { get; set; }

    [ObservableProperty]
    public partial ExchangeTransportOption? SelectedExchangeTransport { get; set; }

    [ObservableProperty]
    public partial string ExchangeUserName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateExchangePasswordCommand))]
    public partial string ExchangePassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateExchangePasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInAgainCommand))]
    public partial bool IsBusy { get; set; }

    public string Address => Account?.Address ?? string.Empty;

    public bool IsExchangeAccount => Account?.ProviderType == MailProviderType.Exchange;

    public bool IsOAuthAccount => Account != null && _providers.GetTraits(Account.ProviderType).UsesInteractiveOAuth;

    public bool IsExchangeModernAuth => IsExchangeAccount && ServerInformation?.UseOAuthAuthentication == true;

    public bool IsExchangePasswordAuth => IsExchangeAccount && ServerInformation != null && !ServerInformation.UseOAuthAuthentication;

    public bool NeedsAttention => Account?.AttentionReason is AccountAttentionReason.InvalidCredentials or AccountAttentionReason.CertificateValidationFailed;

    /// <summary>The organization sign-in needs an embedded browser, which some devices do not have.</summary>
    public bool IsSignInAgainVisible => IsOAuthAccount || (IsExchangeModernAuth && _exchangeSignInService?.IsSupported != false);

    public string SignedInTitle => Account == null
        ? string.Empty
        : IsExchangePasswordAuth
            ? Translator.AccountSignIn_PasswordTitle
            : string.Format(Translator.AccountSignIn_SignedInWith, new ProviderDetail(Account.ProviderType, SpecialImapProvider.None).Name);

    public string ExchangeServerDescription => string.Format(Translator.AccountDetailsPage_ExchangeDescription, ExchangeServerHost);

    private string ExchangeServerHost => AccountAppPresenter.GetHost(ServerInformation?.IncomingServer);

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
            return;

        await ExecuteUIThread(() => ApplyAccount(account));
    }

    private void ApplyAccount(MailAccount account)
    {
        _isLoading = true;

        try
        {
            Account = account;
            ServerInformation = account.ProviderType == MailProviderType.Exchange ? account.ServerInformation : null;
            SelectedExchangeTransport = ExchangeTransportOptions.FirstOrDefault(option => option.Transport == ServerInformation?.ExchangeTransport)
                                        ?? ExchangeTransportOptions[0];

            var userName = ServerInformation?.IncomingServerUsername?.Trim() ?? string.Empty;
            ExchangeUserName = string.Equals(userName, account.Address, StringComparison.OrdinalIgnoreCase) ? string.Empty : userName;
            ExchangePassword = string.Empty;
        }
        finally
        {
            _isLoading = false;
        }

        UpdateExchangePasswordCommand.NotifyCanExecuteChanged();
        SignInAgainCommand.NotifyCanExecuteChanged();
    }

    private bool CanSignInAgain() => IsSignInAgainVisible && !IsBusy && _reauthenticationService != null;

    /// <summary>Runs the provider or organization sign-in again, the same way Fix account does.</summary>
    [RelayCommand(CanExecute = nameof(CanSignInAgain))]
    private async Task SignInAgainAsync()
    {
        var account = Account;
        var reauthenticationService = _reauthenticationService;
        if (account == null || reauthenticationService == null)
            return;

        IsBusy = true;

        try
        {
            var updated = await reauthenticationService.ReauthenticateAsync(account.Id);
            account.AttentionReason = updated.AttentionReason;
            OnPropertyChanged(nameof(NeedsAttention));

            _dialogService.InfoBarMessage(Translator.Info_AccountIssueFixSuccessTitle, Translator.Info_AccountIssueFixAppliedMessage, InfoBarMessageType.Success);

            await reauthenticationService.SynchronizeAfterReauthenticationAsync(account.Id);
        }
        catch (AccountSetupCanceledException)
        {
            _dialogService.InfoBarMessage(Translator.AccountSetup_Failure_SignInTitle, Translator.AccountSetup_Failure_SignInCancelled, InfoBarMessageType.Warning);
        }
        catch (ExchangeSignInException ex)
        {
            _dialogService.InfoBarMessage(Translator.AccountSetup_Failure_SignInTitle, ex.Message, InfoBarMessageType.Error);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Signing account {AccountId} in again failed.", account.Id);
            _dialogService.InfoBarMessage(Translator.AccountSetup_Failure_SignInTitle, string.Format(Translator.AccountSetup_Failure_SignIn, ex.Message), InfoBarMessageType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedExchangeTransportChanged(ExchangeTransportOption? oldValue, ExchangeTransportOption? newValue)
    {
        if (_isLoading || oldValue == null || newValue == null || ReferenceEquals(oldValue, newValue))
            return;

        _ = ChangeExchangeTransportAsync(oldValue, newValue);
    }

    /// <summary>
    /// Saves a new connection method and forgets the detected one. The two transports key mail differently, so a
    /// change of the transport in use downloads the mailbox again; the user confirms that first.
    /// </summary>
    private async Task ChangeExchangeTransportAsync(ExchangeTransportOption previous, ExchangeTransportOption selected)
    {
        var account = Account;
        var server = ServerInformation;
        if (account == null || server == null || !IsExchangeAccount || server.ExchangeTransport == selected.Transport)
            return;

        var updated = ExchangeTransportChange.Prepare(server, selected.Transport, out var error);
        if (updated == null)
        {
            RestoreExchangeTransport(previous);
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, error ?? Translator.ExchangeSetup_ServerAddressInvalid, InfoBarMessageType.Error);
            return;
        }

        var isTransportSwitch = updated.EffectiveExchangeTransport != server.EffectiveExchangeTransport;
        if (isTransportSwitch &&
            !await _dialogService.ShowConfirmationDialogAsync(
                Translator.AccountDetailsPage_ExchangeTransportChangeMessage,
                Translator.AccountDetailsPage_ExchangeTransportChangeTitle,
                Translator.Buttons_Apply))
        {
            RestoreExchangeTransport(previous);
            return;
        }

        IsBusy = true;

        try
        {
            await ExchangeTransportChange.ApplyAsync(_accountService, _synchronizationManager, Messenger, account, updated, isTransportSwitch);
            ServerInformation = updated;
            _dialogService.InfoBarMessage(Translator.EditAccountDetailsPage_SaveSuccess_Title, Translator.AccountDetailsPage_ExchangeTransportSaved, InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Changing the Exchange connection method of {AccountId} failed.", account.Id);
            RestoreExchangeTransport(previous);
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUpdateExchangePassword() => IsExchangePasswordAuth && !IsBusy && !string.IsNullOrEmpty(ExchangePassword);

    /// <summary>Checks the new password against the server before it replaces the stored one.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateExchangePassword))]
    private async Task UpdateExchangePasswordAsync()
    {
        var account = Account;
        var server = ServerInformation;
        if (account == null || server == null)
            return;

        var updated = server.CloneForCredentialStorage();
        var userName = ExchangeUserName?.Trim();
        updated.IncomingServerUsername = string.IsNullOrEmpty(userName) ? account.Address : userName;
        updated.IncomingServerPassword = ExchangePassword;

        IsBusy = true;

        try
        {
            if (_exchangeConnectionTester != null)
            {
                var result = await _exchangeConnectionTester.TestAsync(new MailAccount
                {
                    Id = account.Id,
                    Name = account.Name,
                    Address = account.Address,
                    ProviderType = MailProviderType.Exchange,
                    ServerInformation = updated
                });

                if (!result.IsSuccess)
                {
                    var message = result.Status == ExchangeConnectionTestStatus.CredentialRejected
                        ? string.Format(Translator.AccountSetup_Failure_PasswordRejected, ExchangeServerHost)
                        : string.Format(Translator.AccountSetup_Failure_Connection, ExchangeServerHost, result.FailureReason ?? string.Empty);

                    _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, message, InfoBarMessageType.Error);
                    return;
                }
            }

            await _accountService.UpdateAccountCustomServerInformationAsync(updated);
            account.ServerInformation = updated;
            ServerInformation = updated;

            if (account.AttentionReason == AccountAttentionReason.InvalidCredentials)
            {
                account.AttentionReason = AccountAttentionReason.None;
                await _accountService.UpdateAccountAsync(account);
                OnPropertyChanged(nameof(NeedsAttention));
            }

            await _synchronizationManager.DestroySynchronizerAsync(account.Id);

            ExchangePassword = string.Empty;
            RequestMailSynchronization(account);
            _dialogService.InfoBarMessage(Translator.EditAccountDetailsPage_SaveSuccess_Title, Translator.AccountDetailsPage_ExchangePasswordUpdated, InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Updating the Exchange password of {AccountId} failed.", account.Id);
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RequestMailSynchronization(MailAccount account)
    {
        if (!account.IsMailAccessGranted)
            return;

        Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }));
    }

    private void RestoreExchangeTransport(ExchangeTransportOption previous)
    {
        _isLoading = true;

        try
        {
            SelectedExchangeTransport = previous;
        }
        finally
        {
            _isLoading = false;
        }
    }
}
