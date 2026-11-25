#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Data;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>
/// Changes one app of an account the same way from every page and head: asks the one question the change needs,
/// applies it through <see cref="IAccountCapabilityService"/>, reports a failure with Retry, and requests the
/// synchronization the new source needs.
/// </summary>
public sealed class AccountAppChangeCoordinator
{
    private readonly IMessenger _messenger;
    private readonly IMailDialogService _dialogService;
    private readonly IAccountCapabilityService _capabilityService;
    private readonly IAccountService _accountService;
    private readonly IMailProviderRegistry _providers;

    public AccountAppChangeCoordinator(
        IMessenger messenger,
        IMailDialogService dialogService,
        IAccountCapabilityService capabilityService,
        IAccountService accountService,
        IMailProviderRegistry providers)
    {
        _messenger = messenger;
        _dialogService = dialogService;
        _capabilityService = capabilityService;
        _accountService = accountService;
        _providers = providers;
    }

    public MailProviderTraits GetTraits(MailAccount account) => _providers.GetTraits(account.ProviderType);

    /// <summary>
    /// Whether turning the app to <paramref name="mode"/> needs a server address the account does not have yet:
    /// IMAP mail without its servers, or a CalDAV or CardDAV source without its address.
    /// </summary>
    public bool RequiresServerSetup(MailAccount account, AccountAppState state, AccountCapabilityMode mode)
    {
        if (mode != AccountCapabilityMode.Provider)
            return false;

        if (state.App == WinoApplicationMode.Mail)
            return !GetTraits(account).ServesModesFromProvider && string.IsNullOrWhiteSpace(account.ServerInformation?.IncomingServer);

        return state.IsServiceDavServer && !state.HasDavServer;
    }

    /// <summary>Opens Sign-in and servers with the server the change needs; its Save applies the change.</summary>
    public void OpenServerSetup(MailAccount account, WinoApplicationMode app, AccountCapabilityMode mode)
        => _messenger.Send(new BreadcrumbNavigationRequested(
            Translator.AccountHub_SignInAndServersTitle,
            WinoPage.ImapCalDavSettingsPage,
            ImapCalDavSettingsNavigationContext.CreateForEditMode(account.Id, app, mode)));

    /// <summary>Opens Sign-in and servers of the account: the provider sign-in page or the IMAP and DAV server page.</summary>
    public void OpenSignIn(MailAccount account)
    {
        if (account.IsLocalOnly)
            return;

        if (GetTraits(account).ServesModesFromProvider)
        {
            _messenger.Send(new BreadcrumbNavigationRequested(Translator.AccountHub_SignInAndServersTitle, WinoPage.AccountSignInPage, account.Id));
            return;
        }

        _messenger.Send(new BreadcrumbNavigationRequested(
            Translator.AccountHub_SignInAndServersTitle,
            WinoPage.ImapCalDavSettingsPage,
            ImapCalDavSettingsNavigationContext.CreateForEditMode(account.Id)));
    }

    /// <summary>Returns the stored account after the change, or null when the user cancelled or the change failed.</summary>
    public async Task<MailAccount?> ChangeAsync(Guid accountId, WinoApplicationMode app, AccountCapabilityMode mode, Action? retry = null)
    {
        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
            return null;

        var traits = GetTraits(account);
        var state = AccountApps.Evaluate(account, traits, app);
        var transition = AccountApps.GetTransition(state.Mode, mode);

        if (transition == AccountAppTransition.None)
            return account;

        if (!await ConfirmAsync(account, traits, state, mode, transition))
            return null;

        try
        {
            var updated = await _capabilityService.SetAppModeAsync(account, app, mode);
            RequestSynchronization(updated, app, mode);
            return updated;
        }
        catch (AccountSetupCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Changing {App} of account {AccountId} to {Mode} failed", app, accountId, mode);

            var title = string.Format(Translator.AccountApps_ChangeFailedTitle, AccountAppPresenter.GetTitle(app));
            if (retry == null)
                _dialogService.InfoBarMessage(title, ex.Message, InfoBarMessageType.Error);
            else
                _dialogService.InfoBarMessage(title, ex.Message, InfoBarMessageType.Error, Translator.Buttons_Retry, retry);

            return null;
        }
    }

    private async Task<bool> ConfirmAsync(
        MailAccount account,
        MailProviderTraits traits,
        AccountAppState state,
        AccountCapabilityMode mode,
        AccountAppTransition transition)
    {
        var appTitle = AccountAppPresenter.GetTitle(state.App);
        var items = AccountAppPresenter.GetItemsNoun(state.App);
        var address = string.IsNullOrWhiteSpace(account.Address) ? account.Name : account.Address;

        switch (transition)
        {
            case AccountAppTransition.TurnOff:
                {
                    var message = state.Mode == AccountCapabilityMode.Local
                        ? string.Format(Translator.AccountApps_TurnOffDeviceMessage, items, address)
                        : string.Format(Translator.AccountApps_TurnOffServerMessage, items, address, AccountAppPresenter.GetServerName(account, state));

                    var accounts = await _accountService.GetAccountsAsync();
                    var isProvidedElsewhere = accounts.Any(other => other.Id != account.Id && AppModeReadiness.IsModeEnabled(other, state.App));
                    message += Environment.NewLine + Environment.NewLine + string.Format(
                        isProvidedElsewhere ? Translator.AccountApps_AppStaysMessage : Translator.AccountApps_AppDisappearsMessage,
                        appTitle);

                    return await _dialogService.ShowConfirmationDialogAsync(
                        message,
                        string.Format(Translator.AccountApps_TurnOffTitle, appTitle),
                        Translator.AccountApps_TurnOffButton);
                }
            case AccountAppTransition.LeaveServer:
                return await _dialogService.ShowConfirmationDialogAsync(
                    string.Format(Translator.AccountApps_LeaveServerMessage, items, AccountAppPresenter.GetServerName(account, state), appTitle),
                    string.Format(Translator.AccountApps_LeaveServerTitle, appTitle),
                    Translator.AccountApps_SwitchButton);
            case AccountAppTransition.JoinServer:
                {
                    var serverName = AccountAppPresenter.GetServerName(account, state);
                    var message = state.App == WinoApplicationMode.Calendar
                        ? string.Format(Translator.AccountApps_JoinServerCalendarMessage, serverName)
                        : string.Format(Translator.AccountApps_JoinServerKeepMessage, items, serverName);

                    return await _dialogService.ShowConfirmationDialogAsync(
                        message,
                        string.Format(Translator.AccountApps_JoinServerTitle, appTitle, AccountAppPresenter.GetServiceName(account, state)),
                        Translator.AccountApps_SwitchButton);
                }
            default:
                return true;
        }
    }

    private void RequestSynchronization(MailAccount account, WinoApplicationMode app, AccountCapabilityMode mode)
    {
        if (mode == AccountCapabilityMode.Off)
            return;

        switch (app)
        {
            case WinoApplicationMode.Mail:
                _messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                }));
                break;
            case WinoApplicationMode.Calendar when mode == AccountCapabilityMode.Provider:
                _messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = CalendarSynchronizationType.CalendarEvents
                }));
                break;
        }
    }
}
