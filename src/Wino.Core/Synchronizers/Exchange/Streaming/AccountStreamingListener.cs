using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Shared;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange.Streaming;

/// <summary>
/// Maintains one long-lived EWS streaming-notification connection for a single Exchange account, reopens
/// it across the server's 30-minute lifetime cap, and forwards a debounced batch of changes to a dispatch
/// callback (which routes them into the existing sync pipeline).
/// </summary>
internal sealed class AccountStreamingListener : IAccountNotificationListener
{
    // Exchange2013_SP1 is the schema the EWS Managed API exposes; streaming requires 2010_SP1+.
    private const ExchangeVersion TargetExchangeVersion = ExchangeVersion.Exchange2013_SP1;

    // EWS caps a streaming connection at 30 minutes; OnDisconnect fires and we reopen.
    private const int ConnectionLifetimeMinutes = 30;

    // Coalesce a burst (a new mail fires Created + NewMail + Modified) into one routing pass. Sized so
    // reading through mail (each read echoes a Modified event back from the server) batches into few
    // syncs instead of one per message.
    private const int DebounceMilliseconds = 4000;

    // Back-off before retrying after a failed reconnect (transient network / server blip).
    private const int ReconnectBackoffSeconds = 30;

    private readonly MailAccount _account;
    private readonly IExchangeAuthenticator _authenticator;
    private readonly Func<IReadOnlyCollection<StreamingChange>, Task> _dispatch;
    private readonly ILogger _logger = Log.ForContext<AccountStreamingListener>();

    private readonly object _bufferLock = new();
    private readonly List<StreamingChange> _buffer = new();
    private readonly Timer _debounceTimer;
    private readonly Timer _reconnectTimer;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    private ExchangeService _service;
    private StreamingSubscription _subscription;
    private StreamingSubscriptionConnection _connection;
    private volatile bool _stopped;
    private volatile bool _connected;
    private int _interruptionCount;

    public bool IsConnected => _connected && !_stopped;
    public int InterruptionCount => Volatile.Read(ref _interruptionCount);

    public AccountStreamingListener(
        MailAccount account,
        IExchangeAuthenticator authenticator,
        Func<IReadOnlyCollection<StreamingChange>, Task> dispatch)
    {
        _account = account;
        _authenticator = authenticator;
        _dispatch = dispatch;
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _reconnectTimer = new Timer(_ => _ = ReconnectAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        _stopped = false;
        await OpenAsync().ConfigureAwait(false);
    }

    public void Stop()
    {
        _stopped = true;
        CloseConnection();
        _debounceTimer.Dispose();
        _reconnectTimer.Dispose();
        _reconnectLock.Dispose();
    }

    private async Task OpenAsync()
    {
        var server = _account.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var credentials = await _authenticator.GetCredentialsAsync(_account).ConfigureAwait(false);

        _service = new ExchangeService(TargetExchangeVersion)
        {
            Credentials = credentials,
            Url = new Uri(server.IncomingServer)
        };

        // Route the streaming subscription to the owning mailbox's backend (mirrors CreateServiceAsync). Without
        // it, multi-CAS/DAG deployments can proxy the long-poll to a non-owning CAS and miss events / hit
        // ErrorSubscriptionNotFound after failover.
        if (!string.IsNullOrEmpty(_account.Address))
            _service.HttpHeaders["X-AnchorMailbox"] = _account.Address;

        // "All folders" covers the whole mailbox tree (mail, Calendar, Contacts, Tasks) and auto-includes
        // folders created later.
        _subscription = await _service.SubscribeToStreamingNotificationsOnAllFolders(
            CancellationToken.None,
            EventType.NewMail,
            EventType.Created,
            EventType.Modified,
            EventType.Moved,
            EventType.Deleted).ConfigureAwait(false);

        _connection = new StreamingSubscriptionConnection(_service, ConnectionLifetimeMinutes);
        _connection.AddSubscription(_subscription);
        _connection.OnNotificationEvent += OnNotificationEvent;
        _connection.OnSubscriptionError += OnSubscriptionError;
        _connection.OnDisconnect += OnDisconnect;
        _connection.Open();
        _connected = true;

        _logger.Information("EWS streaming connection opened for {Account} ({Url}).", _account.Name, server.IncomingServer);
    }

    private void OnNotificationEvent(object sender, NotificationEventArgs args)
    {
        if (_stopped)
            return;

        var changes = new List<StreamingChange>();

        foreach (var notification in args.Events)
        {
            switch (notification)
            {
                // Only structural folder changes need a hierarchy reconcile. A folder Modified event (e.g.
                // Inbox's unread/total count changing when mail arrives) is already handled by the item sync.
                case FolderEvent folderEvent when folderEvent.EventType is EventType.Created or EventType.Moved or EventType.Deleted:
                    changes.Add(new StreamingChange(folderEvent.FolderId?.UniqueId, true));
                    break;
                case ItemEvent itemEvent:
                    changes.Add(new StreamingChange(itemEvent.ParentFolderId?.UniqueId, false));
                    break;
            }
        }

        if (changes.Count == 0)
            return;

        _logger.Debug("EWS streaming: {Count} event(s) for {Account}.", changes.Count, _account.Name);

        lock (_bufferLock)
            _buffer.AddRange(changes);

        try
        {
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Stop() disposed the debounce timer concurrently; the batch is moot.
        }
    }

    private void Flush()
    {
        if (_stopped)
            return;

        List<StreamingChange> batch;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                return;

            batch = new List<StreamingChange>(_buffer);
            _buffer.Clear();
        }

        _ = DispatchAsync(batch);
    }

    private async Task DispatchAsync(IReadOnlyCollection<StreamingChange> batch)
    {
        try
        {
            await _dispatch(batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to route streaming changes for {Account}.", _account.Name);
        }
    }

    private void OnSubscriptionError(object sender, SubscriptionErrorEventArgs args)
    {
        // A subscription-level error usually means the subscription is dead; reconnect rebuilds it.
        _logger.Warning(args.Exception, "EWS streaming subscription error for {Account}; reconnecting.", _account.Name);
        _ = ReconnectAsync();
    }

    private async void OnDisconnect(object sender, SubscriptionErrorEventArgs args)
    {
        // The 30-minute lifetime was reached (or the connection dropped); reopen with a fresh service.
        // async void (EWS event handler): contain anything ReconnectAsync doesn't so a throw can't crash the app.
        try
        {
            await ReconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "EWS streaming OnDisconnect handler failed for {Account}.", _account.Name);
        }
    }

    // Reopens the connection with a FRESH ExchangeService (serialized so a concurrent OnDisconnect/
    // OnSubscriptionError don't race). EWS forbids mutating Credentials/cookies on a service that has
    // already sent a request, so we can't refresh the token on the existing service to "resume"; always
    // rebuild. On a hard failure we back off and retry; a rejected refresh token stops the listener.
    private async Task ReconnectAsync()
    {
        if (_stopped || !await _reconnectLock.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            if (_stopped)
                return;

            CloseConnection();
            await OpenAsync().ConfigureAwait(false);
        }
        catch (ExchangeInteractiveSignInRequiredException ex)
        {
            _logger.Warning(ex, "EWS streaming for {Account} needs interactive sign-in; stopping listener.", _account.Name);
            Stop();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "EWS streaming reconnect failed for {Account}; retrying in {Seconds}s.",
                _account.Name, ReconnectBackoffSeconds);

            if (!_stopped)
                _reconnectTimer.Change(TimeSpan.FromSeconds(ReconnectBackoffSeconds), Timeout.InfiniteTimeSpan);
        }
        finally
        {
            // Stop() (which Dispose()s the lock) can run on the interactive-sign-in path above while we still hold
            // it, so tolerate a disposed semaphore here.
            try
            {
                _reconnectLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void CloseConnection()
    {
        var connection = _connection;
        _connection = null;
        if (connection == null)
            return;

        // Each reopen builds a new subscription, so whatever changed in between was not pushed.
        if (_connected)
        {
            _connected = false;
            Interlocked.Increment(ref _interruptionCount);
        }

        connection.OnNotificationEvent -= OnNotificationEvent;
        connection.OnSubscriptionError -= OnSubscriptionError;
        connection.OnDisconnect -= OnDisconnect;

        // StreamingSubscriptionConnection.Close() blocks until the in-flight long-poll/heartbeat winds down
        // (up to ~30s), which would freeze app exit (Stop is called from ExitApplication) and stall a
        // reconnect. Close on a background thread so the caller returns immediately; if the process exits
        // first the OS tears down the socket and the server-side subscription times out on its own.
        Task.Run(() =>
        {
            try
            {
                if (connection.IsOpen)
                    connection.Close();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error while closing EWS streaming connection for {Account}.", _account.Name);
            }
        });
    }
}
