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
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The Exchange settings of an Exchange account: its connection method, and the sign-in it was added with, a password
/// that can be replaced or an organization sign-in that can run again.
/// </summary>
public partial class AccountDetailsPageViewModel
{
    private bool _isLoadingExchangeSettings;

    public List<ExchangeTransportOption> ExchangeTransportOptions { get; } = ExchangeTransportOption.CreateAll();

    [ObservableProperty]
    public partial ExchangeTransportOption? SelectedExchangeTransport { get; set; }

    [ObservableProperty]
    public partial bool IsExchangeSettingsExpanded { get; set; }

    [ObservableProperty]
    public partial string ExchangeUserName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateExchangePasswordCommand))]
    public partial string ExchangePassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateExchangePasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInExchangeAgainCommand))]
    public partial bool IsExchangeBusy { get; set; }

    public bool IsExchangeAccount => Account?.ProviderType == MailProviderType.Exchange;
    public bool IsExchangeModernAuth => IsExchangeAccount && ServerInformation?.UseOAuthAuthentication == true;
    public bool IsExchangePasswordAuth => IsExchangeAccount && ServerInformation != null && !ServerInformation.UseOAuthAuthentication;

    /// <summary>The organization sign-in needs an embedded browser, which some devices do not have (iOS).</summary>
    public bool IsExchangeSignInAgainSupported => _exchangeSignInService?.IsSupported != false;

    public string ExchangeSignInDescription => IsExchangeSignInAgainSupported
        ? Translator.AccountDetailsPage_ExchangeSignInDescription
        : Translator.AccountDetailsPage_ExchangeSignInUnavailable;

    public string ExchangeServerDescription => string.Format(Translator.AccountDetailsPage_ExchangeDescription, ExchangeServerHost);

    /// <summary>
    /// Whether the account's modes can be switched here. Exchange serves calendar, contacts and To Do from its server
    /// with the mailbox credential, so switching one on asks for no new consent.
    /// </summary>
    public bool IsCapabilityEditable => IsOAuthCapabilityEditable || IsExchangeAccount;

    /// <summary>Only Gmail and Outlook ask for consent again when the selection changes.</summary>
    public bool IsCapabilityReauthenticationNoticeVisible => IsOAuthCapabilityEditable && IsCapabilitySelectionChanged;

    private string ExchangeServerHost => Uri.TryCreate(ServerInformation?.IncomingServer, UriKind.Absolute, out var uri)
        ? uri.Host
        : ServerInformation?.IncomingServer ?? string.Empty;

    /// <summary>Fills the Exchange settings from the loaded account; Fix account opens them for a refused password.</summary>
    private void LoadExchangeSettings(bool isRepairRequested)
    {
        _isLoadingExchangeSettings = true;

        try
        {
            var server = IsExchangeAccount ? ServerInformation : null;
            SelectedExchangeTransport = ExchangeTransportOptions.FirstOrDefault(option => option.Transport == server?.ExchangeTransport)
                                        ?? ExchangeTransportOptions[0];

            var userName = server?.IncomingServerUsername?.Trim() ?? string.Empty;
            ExchangeUserName = string.Equals(userName, Account?.Address, StringComparison.OrdinalIgnoreCase) ? string.Empty : userName;
            ExchangePassword = string.Empty;
            IsExchangeSettingsExpanded = server != null && isRepairRequested;
        }
        finally
        {
            _isLoadingExchangeSettings = false;
        }

        NotifyExchangeSettingsChanged();
    }

    partial void OnSelectedExchangeTransportChanged(ExchangeTransportOption? oldValue, ExchangeTransportOption? newValue)
    {
        if (_isLoadingExchangeSettings || oldValue == null || newValue == null || ReferenceEquals(oldValue, newValue))
            return;

        _ = ChangeExchangeTransportAsync(oldValue, newValue);
    }

    /// <summary>
    /// Saves a new connection method and forgets the detected one, so an account that once fell back to EWS tries MAPI
    /// over HTTP again. The two transports key mail differently, so a change of the transport in use downloads the
    /// mailbox again; the user confirms that first.
    /// </summary>
    private async Task ChangeExchangeTransportAsync(ExchangeTransportOption previous, ExchangeTransportOption selected)
    {
        var account = Account;
        var server = ServerInformation;
        if (account == null || server == null || !IsExchangeAccount || server.ExchangeTransport == selected.Transport)
            return;

        var updated = server.CloneForCredentialStorage();
        updated.ExchangeTransport = selected.Transport;
        updated.DetectedExchangeTransport = ExchangeTransport.Automatic;

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

        IsExchangeBusy = true;

        try
        {
            await _accountService.UpdateAccountCustomServerInformationAsync(updated);
            account.ServerInformation = updated;
            ServerInformation = updated;

            await _synchronizationManager.DestroySynchronizerAsync(account.Id);

            if (isTransportSwitch)
                await _accountService.DeleteAccountMailCacheAsync(account.Id, AccountCacheResetReason.ExpiredCache);

            RequestExchangeMailSynchronization(account);

            // Calendars, address books and task lists keep the old transport's ids until their own pass re-keys them;
            // until then the change listener's events do not match them.
            if (isTransportSwitch && account.IsCalendarAccessGranted)
            {
                Messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = CalendarSynchronizationType.Strict
                }));
            }

            if (isTransportSwitch)
            {
                Messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions { AccountId = account.Id, Type = ContactSynchronizationType.Full }));
                Messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions { AccountId = account.Id, Type = TaskSynchronizationType.Full }));
            }
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
            IsExchangeBusy = false;
        }
    }

    private bool CanUpdateExchangePassword() => IsExchangePasswordAuth && !IsExchangeBusy && !string.IsNullOrEmpty(ExchangePassword);

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

        IsExchangeBusy = true;

        try
        {
            if (_exchangeConnectionTester != null)
            {
                var result = await _exchangeConnectionTester.TestAsync(new MailAccount
                {
                    Id = Guid.NewGuid(),
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
            }

            await _synchronizationManager.DestroySynchronizerAsync(account.Id);

            ExchangePassword = string.Empty;
            RequestExchangeMailSynchronization(account);
            _dialogService.InfoBarMessage(Translator.EditAccountDetailsPage_SaveSuccess_Title, Translator.AccountDetailsPage_ExchangePasswordUpdated, InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Updating the Exchange password of {AccountId} failed.", account.Id);
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsExchangeBusy = false;
        }
    }

    private bool CanSignInExchangeAgain() => IsExchangeModernAuth && IsExchangeSignInAgainSupported && !IsExchangeBusy && _reauthenticationService != null;

    /// <summary>Runs the organization sign-in again through the same re-authentication Fix account uses.</summary>
    [RelayCommand(CanExecute = nameof(CanSignInExchangeAgain))]
    private async Task SignInExchangeAgainAsync()
    {
        var account = Account;
        var reauthenticationService = _reauthenticationService;
        if (account == null || reauthenticationService == null)
            return;

        IsExchangeBusy = true;

        try
        {
            var updated = await reauthenticationService.ReauthenticateAsync(account.Id);
            account.AttentionReason = updated.AttentionReason;

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
            Log.Warning(ex, "Signing the Exchange account {AccountId} in again failed.", account.Id);
            _dialogService.InfoBarMessage(Translator.AccountSetup_Failure_SignInTitle, string.Format(Translator.AccountSetup_Failure_SignIn, ex.Message), InfoBarMessageType.Error);
        }
        finally
        {
            IsExchangeBusy = false;
        }
    }

    private void RequestExchangeMailSynchronization(MailAccount account)
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
        _isLoadingExchangeSettings = true;

        try
        {
            SelectedExchangeTransport = previous;
        }
        finally
        {
            _isLoadingExchangeSettings = false;
        }
    }

    private void NotifyExchangeSettingsChanged()
    {
        OnPropertyChanged(nameof(IsExchangeAccount));
        OnPropertyChanged(nameof(IsExchangeModernAuth));
        OnPropertyChanged(nameof(IsExchangePasswordAuth));
        OnPropertyChanged(nameof(ExchangeServerDescription));
        UpdateExchangePasswordCommand.NotifyCanExecuteChanged();
        SignInExchangeAgainCommand.NotifyCanExecuteChanged();
    }
}
