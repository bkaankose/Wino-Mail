#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Synchronizers.Exchange.Streaming;
using Wino.Mapi;
using Wino.Mapi.Rops;
using Wino.Mapi.Transport;
using Wino.Mapi.Wire;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Server push over MAPI/HTTP, replacing the EWS streaming subscription. One long-lived session per
/// account subscribes to the whole store (RopRegisterNotification), then loops on the NotificationWait
/// long-poll; each wake-up collects the pending RopNotify responses with an empty Execute and hands the
/// folders they name to the same router the EWS listener uses.
///
/// Reconnects with backoff on any failure; the debounce coalesces a burst (a new mail fires Created,
/// NewMail and Modified) into one routing pass.
/// </summary>
internal sealed class MapiNotificationListener : IAccountNotificationListener
{
    private const int DebounceMilliseconds = 2000;
    private const int ReconnectBackoffSeconds = 30;
    private const string UserAgent = "WinoMail/MAPI";

    private readonly MailAccount _account;
    private readonly IExchangeAuthenticator _authenticator;
    private readonly Func<IReadOnlyCollection<StreamingChange>, Task> _dispatch;
    private readonly ILogger _logger = Log.ForContext<MapiNotificationListener>();

    private readonly object _bufferLock = new();
    private readonly List<StreamingChange> _buffer = [];
    private readonly Timer _debounceTimer;

    private CancellationTokenSource? _loop;
    private MapiEndpointInfo? _endpoint;
    private volatile bool _connected;
    private int _interruptionCount;

    public bool IsConnected => _connected;
    public int InterruptionCount => Volatile.Read(ref _interruptionCount);

    public MapiNotificationListener(MailAccount account, IExchangeAuthenticator authenticator, Func<IReadOnlyCollection<StreamingChange>, Task> dispatch)
    {
        _account = account;
        _authenticator = authenticator;
        _dispatch = dispatch;
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync()
    {
        _loop?.Cancel();
        _loop = new CancellationTokenSource();
        _ = RunAsync(_loop.Token);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _loop?.Cancel();
        _debounceTimer.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ExchangeInteractiveSignInRequiredException ex)
            {
                _logger.Warning(ex, "MAPI notifications for {Account} need interactive sign-in; stopping listener.", _account.Name);
                return;
            }
            catch (MapiNotAdvertisedException ex)
            {
                // The server offers no MAPI/HTTP; the synchronizer records EWS on the account. The EWS
                // streaming listener takes over at the next app start; until then sync is poll-driven.
                _logger.Warning("MAPI notifications for {Account}: {Reason} Stopping listener.", _account.Name, ex.Message);
                return;
            }
            catch (MapiTransportException ex) when (ex.IsUnauthorized)
            {
                // The bearer token expired under a long-lived session (about an hour). Reconnecting mints a
                // fresh one; no reason to wait.
                _logger.Information("MAPI notification session for {Account}: credential expired; reconnecting now.", _account.Name);
                continue;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "MAPI notification session for {Account} ended; reconnecting in {Seconds}s.", _account.Name, ReconnectBackoffSeconds);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ReconnectBackoffSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var credential = await ResolveCredentialAsync().ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);

        var transport = new MapiHttpTransport(endpoint.MailStoreUrl, credential, UserAgent, line => _logger.Debug("MAPI notify {Account} {Line}", _account.Address, line));
        await using var session = await MapiSession.OpenAsync(transport, endpoint.LegacyDn, cancellationToken).ConfigureAwait(false);

        // Subscribe to the whole store. The subscription's own handle lands in slot 1 and is kept for
        // the session's lifetime; releasing it would end the subscription.
        var (rops, _) = await session.ExecuteAsync(
            RopNotify.BuildRegisterNotification(0, 1, RopNotify.NotificationTypes.StoreChanges),
            [session.LogonHandle, RopExecute.NullHandle], cancellationToken).ConfigureAwait(false);
        RopNotify.ParseRegisterNotification(new RopReader(rops));

        _logger.Information("MAPI notification session opened for {Account}.", _account.Name);
        _connected = true;

        try
        {
            await PumpNotificationsAsync(session, transport, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connected = false;
            Interlocked.Increment(ref _interruptionCount);
        }
    }

    private async Task PumpNotificationsAsync(MapiSession session, MapiHttpTransport transport, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Returns when the server has something, or after its own (up to five-minute) timeout.
            var pending = await transport.NotificationWaitAsync(cancellationToken).ConfigureAwait(false);
            if (!pending)
                continue;

            // An Execute with no ROPs of its own; the response buffer is the pending RopNotify list.
            var (notifyRops, _) = await session.ExecuteAsync([], [session.LogonHandle], cancellationToken).ConfigureAwait(false);

            List<RopNotify.Notification> notifications;
            try
            {
                notifications = RopNotify.ParseNotifications(notifyRops);
            }
            catch (MapiFormatException ex)
            {
                // A shape this parser does not know yet. The session is fine; the safe reaction is a
                // hierarchy-plus-inbox refresh rather than losing push for 30 seconds.
                _logger.Warning(ex, "MAPI notify {Account}: notification buffer not fully decoded ({Bytes} bytes); requesting a broad refresh.", _account.Address, notifyRops.Length);
                Buffer([new StreamingChange(null, true)]);
                continue;
            }

            if (notifications.Count == 0)
                continue;

            var changes = new List<StreamingChange>();
            foreach (var notification in notifications)
            {
                if (notification.IsTable || notification.FolderId is not { } folderId)
                    continue;

                // A message event names the folder it happened in: refresh that folder's items. A folder
                // event is a hierarchy change only for Created/Deleted/Moved/Copied; a folder Modified is its
                // counts or properties changing (every mail arrival raises one), which is an item refresh of
                // that folder, not a tree rebuild.
                var isHierarchy = !notification.IsMessage
                    && notification.Type is RopNotify.NotificationTypes.ObjectCreated or RopNotify.NotificationTypes.ObjectDeleted
                                            or RopNotify.NotificationTypes.ObjectMoved or RopNotify.NotificationTypes.ObjectCopied;

                changes.Add(new StreamingChange(null, isHierarchy, folderId.ToString("X16")));
            }

            _logger.Debug("MAPI notify {Account}: {Count} notifications -> {Changes} changes ({Types}).",
                _account.Address, notifications.Count, changes.Count, string.Join(",", notifications.Select(n => n.Type).Distinct()));

            if (changes.Count > 0)
                Buffer(changes);
        }
    }

    private void Buffer(IEnumerable<StreamingChange> changes)
    {
        lock (_bufferLock)
        {
            foreach (var change in changes)
            {
                if (!_buffer.Contains(change))
                    _buffer.Add(change);
            }
        }

        try
        {
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Flush()
    {
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
            _logger.Error(ex, "Failed to route MAPI notifications for {Account}.", _account.Name);
        }
    }

    private async Task<MapiCredential> ResolveCredentialAsync()
    {
        var token = await _authenticator.TryGetBearerTokenAsync(_account).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            return new MapiCredential.Bearer(token);

        var credentials = await _authenticator.GetCredentialsAsync(_account).ConfigureAwait(false);
        return new MapiCredential.Integrated("NTLM", (credentials as WebCredentials)?.Credentials as NetworkCredential);
    }

    private async Task<MapiEndpointInfo> ResolveEndpointAsync(MapiCredential credential, CancellationToken cancellationToken)
    {
        if (_endpoint is not null)
            return _endpoint;

        var ewsUri = new Uri(_account.ServerInformation!.IncomingServer);
        var autodiscoverUrl = new Uri($"{ewsUri.Scheme}://{ewsUri.Host}/autodiscover/autodiscover.xml");
        _endpoint = await MapiAutodiscover.DiscoverAsync(autodiscoverUrl, _account.Address, credential, cancellationToken).ConfigureAwait(false);
        return _endpoint;
    }
}
