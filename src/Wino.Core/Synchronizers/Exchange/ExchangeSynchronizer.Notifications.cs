using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// The server change listener, the Exchange counterpart of IMAP IDLE: a store-wide subscription that turns server
/// events into folder-scoped sync requests while the app runs. This class holds the supervision (state, reconnects,
/// debounce, routing) and the EWS streaming subscription; the MAPI synchronizer overrides
/// <see cref="ListenForServerChangesAsync"/> with MAPI notifications.
/// </summary>
public partial class ExchangeSynchronizer : IExchangeSynchronizer
{
    // A new mail raises several events (created, new mail, folder modified); one sync pass covers the burst.
    private static readonly TimeSpan ServerChangeDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] ListenerReconnectDelays =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    // EWS closes a streaming connection after at most 30 minutes; the listener reopens it at once.
    private const int StreamingConnectionLifetimeMinutes = 30;

    private readonly SemaphoreSlim _listenerLifecycle = new(1, 1);
    private readonly object _serverChangeLock = new();
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private Task? _serverChangeFlushTask;
    private PendingServerChanges _pendingServerChanges = new();
    private ExchangeNotificationStatus _notificationStatus = ExchangeNotificationStatus.NeverStarted;
    private volatile bool _notificationStatusClosed;

    public ExchangeNotificationStatus NotificationStatus => Volatile.Read(ref _notificationStatus);

    public async Task StartChangeListenerAsync()
    {
        if (Account.ServerInformation == null || Account.IsNetworkAccessBlocked())
            return;

        await _listenerLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDisposing || _listenerTask is { IsCompleted: false })
                return;

            _listenerCancellation?.Dispose();
            _listenerCancellation = new CancellationTokenSource();
            _listenerTask = RunChangeListenerAsync(_listenerCancellation.Token);
        }
        finally
        {
            _listenerLifecycle.Release();
        }
    }

    public async Task StopChangeListenerAsync()
    {
        Task? running;
        await _listenerLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _listenerCancellation?.Cancel();
            running = _listenerTask;
            _listenerTask = null;
        }
        finally
        {
            _listenerLifecycle.Release();
        }

        if (running != null)
        {
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Exchange change listener of {AccountName} did not stop cleanly.", Account.Name);
            }
        }
    }

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync().ConfigureAwait(false);

        // A replacement synchronizer for the same account can start listening before this listener's connection has
        // closed (an EWS streaming close can take seconds). Say "stopped" now and nothing after it, so the late exit of
        // this loop never overwrites the replacement's state in the coordinator.
        SetNotificationStatus(ExchangeNotificationState.Stopped, "synchronizer closed");
        _notificationStatusClosed = true;

        await StopChangeListenerAsync().ConfigureAwait(false);
    }

    private async Task RunChangeListenerAsync(CancellationToken cancellationToken)
    {
        // Yield first so the caller (synchronizer creation) is never held up by the first connection attempt.
        await Task.Yield();

        var failures = 0;
        var catchUpOnNextListen = false;
        string exitReason = "stopped";
        Exception? exitException = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsDisposing)
            {
                if (Account.IsNetworkAccessBlocked())
                {
                    exitReason = "account needs attention";
                    return;
                }

                var sink = new ServerChangeSink(this, () =>
                {
                    failures = 0;
                    SetNotificationStatus(ExchangeNotificationState.Listening, TransportName);

                    // Changes made while the channel was down were never pushed.
                    if (catchUpOnNextListen)
                        QueueServerChange(changes => changes.CatchUp = true);

                    catchUpOnNextListen = false;
                });

                try
                {
                    await ListenForServerChangesAsync(sink, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsPermanentListenerFailure(ex, out var reason))
                {
                    exitReason = reason;
                    exitException = ex;
                    return;
                }
                catch (Exception ex)
                {
                    var delay = ListenerReconnectDelays[Math.Min(failures, ListenerReconnectDelays.Length - 1)];
                    failures++;
                    catchUpOnNextListen = true;
                    SetNotificationStatus(ExchangeNotificationState.Reconnecting, $"{TransportName} failed, retrying in {delay.TotalSeconds:F0} s", ex);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            exitReason = "listener failed";
            exitException = ex;
        }
        finally
        {
            lock (_serverChangeLock)
                _pendingServerChanges = new PendingServerChanges();

            SetNotificationStatus(ExchangeNotificationState.Stopped, exitReason, exitException);
        }
    }

    /// <summary>The channel name used in state reasons and logs.</summary>
    protected virtual string TransportName => "EWS streaming";

    /// <summary>
    /// Holds one subscription until the server closes it (normal return), the token is cancelled, or it fails (throws).
    /// Calls <see cref="ServerChangeSink.Listening"/> once the subscription is in place.
    /// </summary>
    protected virtual async Task ListenForServerChangesAsync(ServerChangeSink sink, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var subscription = await service.SubscribeToStreamingNotificationsOnAllFolders(cancellationToken,
            EventType.NewMail, EventType.Created, EventType.Deleted, EventType.Modified, EventType.Moved, EventType.Copied).ConfigureAwait(false);

        var connection = new StreamingSubscriptionConnection(service, StreamingConnectionLifetimeMinutes);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.AddSubscription(subscription);
        connection.OnNotificationEvent += (_, args) =>
        {
            foreach (var notification in args.Events)
            {
                switch (notification)
                {
                    case FolderEvent folderEvent when folderEvent.EventType is EventType.Created or EventType.Deleted or EventType.Moved or EventType.Copied:
                        sink.HierarchyChanged();
                        break;
                    case ItemEvent itemEvent when itemEvent.ParentFolderId?.UniqueId is { Length: > 0 } parentId:
                        sink.FolderItemsChanged(parentId);
                        break;
                }
            }
        };
        connection.OnSubscriptionError += (_, args) => closed.TrySetResult(args.Exception ?? new InvalidOperationException("The EWS streaming subscription failed."));
        connection.OnDisconnect += (_, args) => closed.TrySetResult(args.Exception);

        try
        {
            connection.Open();
            sink.Listening();

            using (cancellationToken.Register(() => closed.TrySetCanceled(cancellationToken)))
            {
                var failure = await closed.Task.ConfigureAwait(false);
                if (failure != null)
                    throw failure;
            }
        }
        finally
        {
            try
            {
                if (connection.IsOpen)
                    connection.Close();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Closing the EWS streaming connection of {AccountName} failed.", Account.Name);
            }

            connection.Dispose();
        }
    }

    /// <summary>
    /// Failures that a reconnect cannot fix. A rejected password in particular must not be retried: every attempt
    /// counts towards the directory's account lockout.
    /// </summary>
    protected virtual bool IsPermanentListenerFailure(Exception exception, out string reason)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is AuthenticationAttentionException)
            {
                reason = "sign-in needs attention";
                return true;
            }

            var message = current.Message ?? string.Empty;
            if (message.Contains("(401)", StringComparison.Ordinal) || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                reason = "server rejected the credentials";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private void SetNotificationStatus(ExchangeNotificationState state, string reason, Exception? exception = null)
    {
        if (_notificationStatusClosed)
            return;

        var previous = Volatile.Read(ref _notificationStatus);
        var status = new ExchangeNotificationStatus(state, reason, DateTimeOffset.UtcNow);
        Volatile.Write(ref _notificationStatus, status);

        if (previous.State == state && previous.Reason == reason)
            return;

        if (exception == null)
            _logger.Information("Exchange change listener for {AccountName} is {State} ({Reason}).", Account.Name, state, reason);
        else
            _logger.Information(exception, "Exchange change listener for {AccountName} is {State} ({Reason}).", Account.Name, state, reason);

        try
        {
            WeakReferenceMessenger.Default.Send(new ExchangeNotificationStateChanged(Account.Id, status));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not publish the Exchange listener state of {AccountName}.", Account.Name);
        }
    }

    private void QueueServerChange(Action<PendingServerChanges> apply)
    {
        lock (_serverChangeLock)
        {
            if (IsDisposing || _listenerCancellation is not { IsCancellationRequested: false } cancellation)
                return;

            apply(_pendingServerChanges);
            var token = cancellation.Token;
            _serverChangeFlushTask ??= Task.Run(() => FlushServerChangesAsync(token));
        }
    }

    private async Task FlushServerChangesAsync(CancellationToken cancellationToken)
    {
        PendingServerChanges batch;
        try
        {
            await Task.Delay(ServerChangeDebounce, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_serverChangeLock)
                _serverChangeFlushTask = null;
            return;
        }

        lock (_serverChangeLock)
        {
            batch = _pendingServerChanges;
            _pendingServerChanges = new PendingServerChanges();
            _serverChangeFlushTask = null;
        }

        try
        {
            await RouteServerChangesAsync(batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not request synchronization for Exchange changes of {AccountName}.", Account.Name);
        }
    }

    private async Task RouteServerChangesAsync(PendingServerChanges batch)
    {
        if (batch.CatchUp)
        {
            _logger.Information("Exchange change listener for {AccountName}: catching up on changes missed while it was down.", Account.Name);
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions { AccountId = Account.Id, Type = MailSynchronizationType.FullFolders }));
            Messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions { AccountId = Account.Id, Type = CalendarSynchronizationType.CalendarEvents }));
            await RequestContactAndTaskSynchronizationAsync(null).ConfigureAwait(false);
            return;
        }

        if (batch.HierarchyChanged)
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions { AccountId = Account.Id, Type = MailSynchronizationType.FoldersOnly }));

        if (batch.FolderKeys.Count == 0)
            return;

        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var mailFolderIds = folders
            .Where(f => f.IsSynchronizationEnabled && f.RemoteFolderId != null && batch.FolderKeys.Contains(f.RemoteFolderId))
            .Select(f => f.Id)
            .ToList();

        var calendars = await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        var calendarIds = calendars
            .Where(c => c.RemoteCalendarId != null && batch.FolderKeys.Contains(c.RemoteCalendarId))
            .Select(c => c.Id)
            .ToList();

        _logger.Debug("Exchange changes for {AccountName}: {Keys} folder(s) changed, {Mail} mail folder(s) and {Calendars} calendar(s) to synchronize.",
            Account.Name, batch.FolderKeys.Count, mailFolderIds.Count, calendarIds.Count);

        if (mailFolderIds.Count > 0)
        {
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.CustomFolders,
                SynchronizationFolderIds = mailFolderIds
            }));
        }

        if (calendarIds.Count > 0)
        {
            Messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = CalendarSynchronizationType.SingleCalendar,
                SynchronizationCalendarIds = calendarIds
            }));
        }

        await RequestContactAndTaskSynchronizationAsync(batch.FolderKeys).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a sync of the Exchange address books and task lists whose folder changed, or of all of them when
    /// <paramref name="folderKeys"/> is null. Accounts that keep contacts or tasks on the device have no Exchange rows.
    /// </summary>
    private async Task RequestContactAndTaskSynchronizationAsync(IReadOnlySet<string>? folderKeys)
    {
        if (ContactService != null)
        {
            var books = await ContactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false);
            foreach (var book in books.Where(b => b.SourceKind == ContactSourceKind.Exchange && b.RemoteId != null
                                                  && (folderKeys == null || folderKeys.Contains(b.RemoteId))))
            {
                Messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
                {
                    AccountId = Account.Id,
                    Type = ContactSynchronizationType.Full,
                    AddressBookId = book.Id
                }));
            }
        }

        if (TaskService != null)
        {
            var lists = await TaskService.GetTaskListsAsync(Account.Id).ConfigureAwait(false);
            foreach (var list in lists.Where(l => l.SourceKind == TaskSourceKind.Exchange && l.RemoteId != null
                                                  && (folderKeys == null || folderKeys.Contains(l.RemoteId))))
            {
                Messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
                {
                    AccountId = Account.Id,
                    Type = TaskSynchronizationType.Full,
                    TaskListId = list.Id
                }));
            }
        }
    }

    private sealed class PendingServerChanges
    {
        public HashSet<string> FolderKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HierarchyChanged { get; set; }
        public bool CatchUp { get; set; }
    }

    /// <summary>
    /// What a transport-specific listener reports. Folder keys use the local remote-id format of the transport
    /// (the EWS folder id, or "mapi:" plus the folder id), so they match folders and calendars directly.
    /// </summary>
    protected sealed class ServerChangeSink
    {
        private readonly ExchangeSynchronizer _owner;
        private readonly Action _listening;

        internal ServerChangeSink(ExchangeSynchronizer owner, Action listening)
        {
            _owner = owner;
            _listening = listening;
        }

        public void Listening() => _listening();
        public void FolderItemsChanged(string folderKey) => _owner.QueueServerChange(changes => changes.FolderKeys.Add(folderKey));
        public void HierarchyChanged() => _owner.QueueServerChange(changes => changes.HierarchyChanged = true);

        /// <summary>Something changed that could not be attributed to a folder; the next pass synchronizes everything.</summary>
        public void Unattributed() => _owner.QueueServerChange(changes => changes.CatchUp = true);
    }
}
