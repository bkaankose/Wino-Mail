#nullable enable
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Changing the connection method of an Exchange account (MAPI over HTTP, EWS or ActiveSync), shared by every page
/// that offers it so each direction resets the same state.
/// </summary>
internal static class ExchangeTransportChange
{
    /// <summary>The server information for <paramref name="transport"/>, or the message that explains why it cannot be used.</summary>
    public static CustomServerInformation? Prepare(CustomServerInformation server, ExchangeTransport transport, out string? error)
    {
        error = null;
        if (transport == ExchangeTransport.ActiveSync && server.UseOAuthAuthentication)
        {
            error = Translator.ExchangeSetup_ActiveSyncPasswordRequired;
            return null;
        }

        var updated = server.CloneForCredentialStorage();
        updated.ExchangeTransport = transport;
        updated.DetectedExchangeTransport = ExchangeTransport.Automatic;

        var endpoint = ExchangeServerEndpoint.Normalize(updated.IncomingServer ?? string.Empty, transport);
        if (endpoint == null)
        {
            error = Translator.ExchangeSetup_ServerAddressInvalid;
            return null;
        }

        updated.IncomingServer = endpoint.AbsoluteUri;
        return updated;
    }

    /// <summary>
    /// Saves the new connection method and, when the transport in use changes, resets the previous transport's
    /// synchronization state and downloads the mailbox again. Folders keep their ids and settings: the next folder
    /// synchronization re-keys them in place.
    /// </summary>
    public static async Task ApplyAsync(IAccountService accountService, ISynchronizationManager synchronizationManager, IMessenger messenger,
        MailAccount account, CustomServerInformation updated, bool isTransportSwitch)
    {
        await accountService.UpdateAccountCustomServerInformationAsync(updated);
        account.ServerInformation = updated;

        await synchronizationManager.DestroySynchronizerAsync(account.Id);

        if (isTransportSwitch)
        {
            await accountService.ResetExchangeTransportStateAsync(account.Id);
            account.SynchronizationDeltaIdentifier = string.Empty;
        }

        if (account.IsMailAccessGranted)
        {
            messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
        }

        if (!isTransportSwitch)
            return;

        // Calendars, address books and task lists keep the old transport's ids until their own pass re-keys them;
        // until then the change listener's events do not match them.
        if (account.IsCalendarAccessGranted)
        {
            messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.Strict
            }));
        }

        messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions { AccountId = account.Id, Type = ContactSynchronizationType.Full }));
        messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions { AccountId = account.Id, Type = TaskSynchronizationType.Full }));
    }
}
