using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Mapi;

namespace Wino.Core.Synchronizers.Exchange.Streaming;

/// <summary>
/// Starts and tracks one push listener per Exchange account so server-side changes arrive as
/// near-real-time notifications instead of waiting for the periodic poll: the MAPI/HTTP notification
/// session (<see cref="MapiNotificationListener"/>) or, for accounts on the EWS transport, the EWS
/// streaming subscription (<see cref="AccountStreamingListener"/>).
/// </summary>
public sealed class ExchangeStreamingNotificationService : IExchangeStreamingNotificationService
{
    private readonly IAccountService _accountService;
    private readonly IExchangeAuthenticator _authenticator;
    private readonly StreamingEventRouter _router;
    private readonly ILogger _logger = Log.ForContext<ExchangeStreamingNotificationService>();
    private readonly ConcurrentDictionary<Guid, IAccountNotificationListener> _listeners = new();

    public ExchangeStreamingNotificationService(
        IAccountService accountService,
        IExchangeAuthenticator authenticator,
        IFolderService folderService,
        ICalendarService calendarService,
        IContactService contactService = null,
        ITaskService taskService = null)
    {
        _accountService = accountService;
        _authenticator = authenticator;
        _router = new StreamingEventRouter(folderService, calendarService, contactService, taskService);
    }

    public async Task StartAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts.Where(a => a.ProviderType == MailProviderType.Exchange && a.IsMailAccessGranted))
            await StartForAccountAsync(account).ConfigureAwait(false);
    }

    public async Task StartForAccountAsync(MailAccount account)
    {
        if (account == null || account.ProviderType != MailProviderType.Exchange || account.ServerInformation == null)
            return;

        // A listener that ended itself stays in the map as a dead entry: the MAPI one returns for good
        // when the sign-in was rejected or the server advertises no MAPI/HTTP. Starting the account
        // again therefore has to replace a dead one, or push stays down until the next app start. One
        // that is still trying is left exactly as it is, because an account is started again on paths
        // that have nothing to do with its health, and a channel between reconnect attempts is fine.
        if (_listeners.TryGetValue(account.Id, out var previous))
        {
            if (previous.IsRunning)
                return;

            _listeners.TryRemove(account.Id, out _);
            previous.Stop();
        }

        var useEws = account.ServerInformation.EffectiveExchangeTransport == ExchangeTransport.Ews;
        IAccountNotificationListener listener = useEws
            ? new AccountStreamingListener(account, _authenticator, changes => DispatchAsync(account.Id, changes))
            : new MapiNotificationListener(account, _authenticator, changes => DispatchAsync(account.Id, changes));

        if (!_listeners.TryAdd(account.Id, listener))
        {
            listener.Stop();
            return;
        }

        try
        {
            await listener.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start {Transport} notifications for {Account}.", useEws ? "EWS" : "MAPI", account.Name);
            _listeners.TryRemove(account.Id, out _);
        }
    }

    // Route a debounced batch of streaming changes from one account's listener into the existing sync
    // pipeline by sending the same messages the rest of the app already consumes.
    private async Task DispatchAsync(Guid accountId, IReadOnlyCollection<StreamingChange> changes)
    {
        try
        {
            var result = await _router.RouteAsync(accountId, changes).ConfigureAwait(false);
            if (result.IsEmpty)
                return;

            foreach (var mailSync in result.MailSyncs)
                WeakReferenceMessenger.Default.Send(mailSync);

            foreach (var calendarSync in result.CalendarSyncs)
                WeakReferenceMessenger.Default.Send(calendarSync);

            foreach (var contactSync in result.ContactSyncs)
                WeakReferenceMessenger.Default.Send(contactSync);

            foreach (var taskSync in result.TaskSyncs)
                WeakReferenceMessenger.Default.Send(taskSync);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to route streaming changes for account {AccountId}.", accountId);
        }
    }

    // A listener that gave up (sign-in needed, protocol not offered) or is between reconnects stays
    // registered, so being registered is not enough: the channel has to be open.
    public bool IsStreaming(Guid accountId)
        => _listeners.TryGetValue(accountId, out var listener) && listener.IsConnected;

    public int GetInterruptionCount(Guid accountId)
        => _listeners.TryGetValue(accountId, out var listener) ? listener.InterruptionCount : 0;

    public Task StopForAccountAsync(Guid accountId)
    {
        if (_listeners.TryRemove(accountId, out var listener))
            listener.Stop();

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        foreach (var listener in _listeners.Values)
            listener.Stop();

        _listeners.Clear();
        return Task.CompletedTask;
    }
}
