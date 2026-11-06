#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.PushRelay.Socket.Transport;

namespace Wino.PushRelay.Socket;

/// <summary>
/// Keeps one socket to WinoRelay for this device. <c>StartAsync</c> checks the preconditions (device id, relay token,
/// socket key, the relay's socket info), opens the hub and waits for <c>hello</c>; afterwards a dropped socket is reopened
/// with jittered backoff until <c>StopAsync</c>, except after <c>bye revoked|abuse</c>, a repeated <c>bye replaced</c> or a lost sign-in.
/// Encrypted events are decrypted with the device key and the relay key the envelope names.
/// </summary>
/// <remarks>Every public member hops to the thread pool first; events are raised from pool threads.</remarks>
public sealed class WinoRelaySocketClient : IWinoRelaySocketClient
{
    private readonly IPushRelayTokenProvider _tokens;
    private readonly IPushRelayClient _relay;
    private readonly IPushRelayDeviceIdentityStore _identityStore;
    private readonly IPushRelaySocketKeyStore? _keyStore;
    private readonly IRelayHubConnectionFactory _connections;
    private readonly WinoRelaySocketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<WinoRelaySocketClient>();

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private Session? _session;
    private int _state;
    private RelaySocketHello? _lastHello;
    private DateTimeOffset? _lastReplacedReconnectAt;

    public WinoRelaySocketClient(
        IPushRelayTokenProvider tokens,
        IPushRelayClient relay,
        IPushRelayDeviceIdentityStore identityStore,
        IRelayHubConnectionFactory connections,
        IPushRelaySocketKeyStore? keyStore = null,
        WinoRelaySocketOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _keyStore = keyStore;
        _options = options ?? new WinoRelaySocketOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsConnected => State == RelaySocketConnectionState.Connected;

    public RelaySocketConnectionState State
    {
        get => (RelaySocketConnectionState)Volatile.Read(ref _state);
        private set => Volatile.Write(ref _state, (int)value);
    }

    public RelaySocketHello? LastHello => Volatile.Read(ref _lastHello);

    public event EventHandler<RelaySocketHello?>? Connected;
    public event EventHandler<RelaySocketEvent>? EventReceived;
    public event EventHandler<RelaySocketEventFailure>? EventFailed;
    public event EventHandler<RelaySocketDisconnection>? Disconnected;

    public Task<RelaySocketStartResult> StartAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => StartCoreAsync(cancellationToken), cancellationToken);

    public Task StopAsync() => Task.Run(() => StopCoreAsync(null));

    public Task<int> AckAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        if (eventIds.Count is 0 or > RelaySocketProtocol.MaxAckIds)
            throw new ArgumentException($"Pass 1 to {RelaySocketProtocol.MaxAckIds} event ids.", nameof(eventIds));
        var ids = eventIds.ToArray();
        foreach (var id in ids)
        {
            if (!Guid.TryParseExact(id, "D", out var guid) || guid == Guid.Empty)
                throw new ArgumentException($"'{id}' is not a relay event id.", nameof(eventIds));
        }

        return Task.Run(async () =>
        {
            var connection = CurrentConnection();
            if (connection is null || !IsConnected)
                throw new InvalidOperationException("The relay socket is not connected.");
            return await connection.AckAsync(ids, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private IRelayHubConnection? CurrentConnection()
    {
        lock (_gate) return _session?.Connection;
    }

    private async Task<RelaySocketStartResult> StartCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? existing;
            lock (_gate) existing = _session;
            if (existing is not null)
                return new RelaySocketStartResult(IsConnected ? RelaySocketStartOutcome.AlreadyConnected : RelaySocketStartOutcome.AlreadyStarted, Hello: LastHello);

            var identity = await _identityStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is null)
                return new RelaySocketStartResult(RelaySocketStartOutcome.DeviceNotRegistered);

            PushRelayAccessToken token;
            try
            {
                token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PushRelayApiException ex)
            {
                return new RelaySocketStartResult(ex.IsSignInRequired ? RelaySocketStartOutcome.SignInRequired : RelaySocketStartOutcome.Unavailable, ex.Problem, Error: ex);
            }

            PushRelaySocketInfo? socketInfo;
            try
            {
                socketInfo = (await _relay.GetWellKnownAsync(cancellationToken).ConfigureAwait(false)).Socket;
            }
            catch (PushRelayApiException ex)
            {
                return new RelaySocketStartResult(ex.IsSignInRequired ? RelaySocketStartOutcome.SignInRequired : RelaySocketStartOutcome.Unavailable, ex.Problem, Error: ex);
            }

            if (socketInfo is null)
                return new RelaySocketStartResult(RelaySocketStartOutcome.SocketUnavailable);

            var deviceKey = await LoadDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
            if (socketInfo.Keys is { Count: > 0 } && deviceKey is null)
                return new RelaySocketStartResult(RelaySocketStartOutcome.SocketKeyMissing);

            var session = new Session(identity.DeviceId, deviceKey, socketInfo, ResolveHubUri(token.RelayBaseUri, socketInfo.Path));
            lock (_gate) _session = session;
            State = RelaySocketConnectionState.Connecting;

            var result = await ConnectAsync(session, cancellationToken).ConfigureAwait(false);
            if (!result.IsConnected)
            {
                lock (_gate) _session = null;
                State = RelaySocketConnectionState.Stopped;
                session.Lifetime.Cancel();
                await session.DisposeConnectionAsync().ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(RelaySocketDisconnection? final)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        Session? session;
        try
        {
            lock (_gate)
            {
                session = _session;
                _session = null;
            }

            if (session is null)
                return;

            session.Stopping = true;
            session.Lifetime.Cancel();
            var wasConnected = IsConnected;
            State = RelaySocketConnectionState.Stopped;
            Volatile.Write(ref _lastHello, null);

            var reconnect = session.ReconnectTask;
            if (reconnect is not null)
            {
                try { await reconnect.ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _logger.Debug(ex, "Relay socket reconnect loop ended with an error."); }
            }

            await session.DisposeConnectionAsync().ConfigureAwait(false);
            if (wasConnected || final is not null)
                Raise(Disconnected, final ?? new RelaySocketDisconnection(RelaySocketDisconnectReason.Stopped, false));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>One connect attempt for the session: a fresh hub connection, started with a timeout, then the hello wait.</summary>
    private async Task<RelaySocketStartResult> ConnectAsync(Session session, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (session.Lifetime.IsCancellationRequested)
                return new RelaySocketStartResult(RelaySocketStartOutcome.Unavailable, Error: new OperationCanceledException("The socket client was stopped."));

            var settings = new RelayHubConnectionSettings(session.HubUri, session.DeviceId, GetAccessTokenAsync);
            var connection = _connections.Create(settings);
            var helloSource = new TaskCompletionSource<RelaySocketHello>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attemptState = new Attempt(connection, helloSource);
            connection.OnHello(hello => OnHelloAsync(session, attemptState, hello));
            connection.OnEvent(message => OnEventAsync(session, attemptState, message));
            connection.OnBye(reason => OnByeAsync(session, attemptState, reason));
            connection.Closed += error => OnClosedAsync(session, attemptState, error);

            if (!await session.TryAdoptAsync(attemptState).ConfigureAwait(false))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return new RelaySocketStartResult(RelaySocketStartOutcome.Unavailable, Error: new OperationCanceledException("The socket client was stopped."));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
            timeout.CancelAfter(_options.ConnectTimeout);
            try
            {
                await connection.StartAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var failure = Classify(ex, connection.LastProblem, timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested);
                await session.ReleaseAsync(attemptState).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    throw;

                if (failure.Problem is { Status: 401 } problem && problem.Reason is not PushRelayAuthReasons.DeviceRevoked && attempt == 0)
                {
                    // An expired or rejected relay token is renewed once, as the HTTP client does.
                    _tokens.Invalidate();
                    continue;
                }

                _logger.Information("Relay socket connect failed: {Outcome} ({Code}/{Reason}).", failure.Outcome, failure.Problem?.Code, failure.Problem?.Reason);
                return failure;
            }

            State = RelaySocketConnectionState.Connected;
            session.FailedAttempts = 0;
            _logger.Information("Relay socket connected to {HubUri}.", session.HubUri);

            RelaySocketHello? hello = null;
            try
            {
                hello = await helloSource.Task.WaitAsync(_options.HelloTimeout, session.Lifetime.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warning("The relay sent no hello within {Timeout}.", _options.HelloTimeout);
            }
            catch (OperationCanceledException)
            {
                // Stopped while waiting; the stop path reports it.
            }

            if (attemptState.Closed)
            {
                // Closed before or during the hello wait (replaced, revoked...). The close handler decides what follows.
                return new RelaySocketStartResult(RelaySocketStartOutcome.Refused, attemptState.CloseProblem, Error: attemptState.CloseError);
            }

            if (hello is null && !attemptState.HelloRaised)
            {
                attemptState.HelloRaised = true;
                Raise(Connected, null);
            }

            return new RelaySocketStartResult(RelaySocketStartOutcome.Connected, Hello: hello);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        => (await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false)).Token;

    private Task OnHelloAsync(Session session, Attempt attempt, RelaySocketHello hello)
    {
        if (!session.IsCurrent(attempt)) return Task.CompletedTask;
        Volatile.Write(ref _lastHello, hello);
        attempt.Hello.TrySetResult(hello);
        if (!attempt.HelloRaised)
        {
            attempt.HelloRaised = true;
            Raise(Connected, hello);
        }

        if (_options.ReplayUnackedOnConnect && hello.UnackedEvents > 0 && !attempt.ReplayStarted)
        {
            attempt.ReplayStarted = true;
            _ = Task.Run(() => ReplayUnackedAsync(session, attempt));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pages <c>GET /v1/devices/me/events?unacked=true</c> and raises every event the device has not acknowledged, oldest
    /// first, as replayed events. A page failure ends the replay; the next connect tries again.
    /// </summary>
    private async Task ReplayUnackedAsync(Session session, Attempt attempt)
    {
        Guid? since = null;
        var replayed = 0;
        try
        {
            while (!attempt.Closed && !session.Lifetime.IsCancellationRequested)
            {
                var page = await _relay.ListDeviceEventsAsync(session.DeviceId, since, _options.ReplayPageSize, unackedOnly: true, session.Lifetime.Token)
                    .ConfigureAwait(false);
                foreach (var entry in page.Events ?? [])
                {
                    if (entry.AckedAt is not null || attempt.Closed)
                        continue;

                    var payload = new Dictionary<string, string>(entry.Payload ?? [], StringComparer.Ordinal);
                    payload.TryAdd(PushRelayPayloadKeys.EventId, entry.Id.ToString("D"));
                    payload.TryGetValue(PushRelayPayloadKeys.Kind, out var kind);
                    replayed++;
                    Raise(EventReceived, new RelaySocketEvent(payload[PushRelayPayloadKeys.EventId], kind ?? entry.Kind, payload, false, entry.SentAt, IsReplayed: true));
                }

                if (!page.HasMore || page.NextSince is null || page.NextSince == since)
                    break;
                since = page.NextSince;
            }

            _logger.Information("Replayed {Count} unacknowledged relay events over HTTP.", replayed);
        }
        catch (OperationCanceledException)
        {
            // Stopped during the replay.
        }
        catch (PushRelayApiException ex)
        {
            _logger.Warning("The relay event replay stopped after {Count} events: {Code}/{Reason}.", replayed, ex.Code, ex.Problem.Reason);
        }
    }

    private async Task OnEventAsync(Session session, Attempt attempt, Dictionary<string, string> message)
    {
        if (!session.IsCurrent(attempt) || message is null) return;

        IReadOnlyDictionary<string, string> payload = message;
        var encrypted = false;
        if (RelaySocketPayloadCipher.IsEnvelope(message))
        {
            encrypted = true;
            try
            {
                var key = await ResolveKeyAsync(session, message).ConfigureAwait(false);
                payload = RelaySocketPayloadCipher.Decrypt(message, key);
            }
            catch (RelaySocketCryptoException ex)
            {
                _logger.Warning("A relay socket event could not be decrypted: {Reason}", ex.Message);
                Raise(EventFailed, new RelaySocketEventFailure(ex.Message, ex, message));
                return;
            }
        }

        payload.TryGetValue(PushRelayPayloadKeys.EventId, out var eventId);
        payload.TryGetValue(PushRelayPayloadKeys.Kind, out var kind);
        Raise(EventReceived, new RelaySocketEvent(eventId, kind, payload, encrypted, _timeProvider.GetUtcNow()));
    }

    private Task OnByeAsync(Session session, Attempt attempt, string reason)
    {
        if (session.IsCurrent(attempt))
        {
            attempt.ByeReason = reason;
            _logger.Information("The relay is closing the socket: {Reason}.", reason);
        }

        return Task.CompletedTask;
    }

    private async Task OnClosedAsync(Session session, Attempt attempt, Exception? error)
    {
        attempt.Closed = true;
        attempt.CloseError = error;
        attempt.CloseProblem = attempt.Connection.LastProblem;
        attempt.Hello.TrySetCanceled();
        if (session.Stopping || !session.IsCurrent(attempt))
            return;

        Volatile.Write(ref _lastHello, null);
        var reason = attempt.ByeReason switch
        {
            RelaySocketByeReasons.Revoked => RelaySocketDisconnectReason.Revoked,
            RelaySocketByeReasons.Replaced => RelaySocketDisconnectReason.Replaced,
            RelaySocketByeReasons.Abuse => RelaySocketDisconnectReason.Abuse,
            RelaySocketByeReasons.Restart => RelaySocketDisconnectReason.RelayRestarting,
            _ => RelaySocketDisconnectReason.ConnectionLost,
        };
        var reconnect = _options.AutoReconnect && (reason is RelaySocketDisconnectReason.ConnectionLost or RelaySocketDisconnectReason.RelayRestarting
            || (reason == RelaySocketDisconnectReason.Replaced && TryClaimReplacedReconnect()));

        if (reconnect && reason == RelaySocketDisconnectReason.Replaced)
        {
            // bye(replaced) also means the API moved the device to another owner: reconnect with a fresh token.
            _tokens.Invalidate();
        }

        if (!reconnect)
        {
            _ = Task.Run(() => StopCoreAsync(new RelaySocketDisconnection(reason, false, attempt.ByeReason, error, attempt.CloseProblem)));
            return;
        }

        State = RelaySocketConnectionState.Reconnecting;
        _logger.Information("Relay socket closed ({Reason}); reconnecting.", reason);
        Raise(Disconnected, new RelaySocketDisconnection(reason, true, attempt.ByeReason, error, attempt.CloseProblem));
        await session.ReleaseAsync(attempt).ConfigureAwait(false);
        session.ReconnectTask = Task.Run(() => ReconnectLoopAsync(session));
    }

    /// <summary>
    /// The relay sends <c>bye(replaced)</c> both when the device's owner changed (reconnect under the new owner) and when
    /// another socket of this device took over. The first gets one prompt reconnect; a second <c>replaced</c> within
    /// <see cref="WinoRelaySocketOptions.ReplacedReconnectWindow"/> means another socket keeps taking over, so this one stays down.
    /// </summary>
    private bool TryClaimReplacedReconnect()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastReplacedReconnectAt is { } last && now - last < _options.ReplacedReconnectWindow)
                return false;
            _lastReplacedReconnectAt = now;
            return true;
        }
    }

    private async Task ReconnectLoopAsync(Session session)
    {
        while (!session.Lifetime.IsCancellationRequested)
        {
            var delay = NextReconnectDelay(session);
            try
            {
                await Task.Delay(delay, _timeProvider, session.Lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RelaySocketStartResult result;
            try
            {
                result = await ConnectAsync(session, session.Lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (result.IsConnected)
            {
                session.ReconnectTask = null;
                return;
            }

            session.FailedAttempts++;
            session.RetryAfter = result.RetryAfterSeconds is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null;

            var terminal = result.Outcome switch
            {
                RelaySocketStartOutcome.SignInRequired => RelaySocketDisconnectReason.SignInRequired,
                RelaySocketStartOutcome.SocketKeyMissing or RelaySocketStartOutcome.SocketUnavailable => RelaySocketDisconnectReason.Refused,
                RelaySocketStartOutcome.Refused when result.Problem?.Reason is RelaySocketRefusalReasons.PlatformNotAllowed or RelaySocketRefusalReasons.KeyRequired
                    => RelaySocketDisconnectReason.Refused,
                RelaySocketStartOutcome.Refused when result.Problem?.Reason == PushRelayAuthReasons.DeviceRevoked => RelaySocketDisconnectReason.Revoked,
                _ => (RelaySocketDisconnectReason?)null,
            };

            if (terminal is { } reason)
            {
                session.ReconnectTask = null;
                _ = Task.Run(() => StopCoreAsync(new RelaySocketDisconnection(reason, false, null, result.Error, result.Problem)));
                return;
            }
        }
    }

    private TimeSpan NextReconnectDelay(Session session)
    {
        var delays = _options.ReconnectDelays is { Length: > 0 } configured ? configured : [TimeSpan.FromSeconds(5)];
        var baseDelay = delays[Math.Min(session.FailedAttempts, delays.Length - 1)];
        if (session.RetryAfter is { } retryAfter && retryAfter > baseDelay)
            baseDelay = retryAfter > _options.MaxRetryAfter ? _options.MaxRetryAfter : retryAfter;

        // ±20 % so that every desktop does not reconnect in the same second after a relay restart.
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Max(100, baseDelay.TotalMilliseconds * jitter));
    }

    private async Task<byte[]> ResolveKeyAsync(Session session, IReadOnlyDictionary<string, string> envelope)
    {
        if (!envelope.TryGetValue(RelaySocketEnvelopeKeys.KeyId, out var kid) || string.IsNullOrEmpty(kid))
            throw new RelaySocketCryptoException("The envelope names no relay key (kid).");
        if (session.DeviceKey is not { } deviceKey)
            throw new RelaySocketCryptoException("This device has no socket key, so encrypted events cannot be read.");

        if (session.TryGetDerivedKey(kid, out var cached))
            return cached;

        var relayKey = session.SocketInfo.Keys?.FirstOrDefault(k => k.Kid == kid);
        if (relayKey is null)
        {
            // A rotated relay key: the well-known document has the new one. Refreshes are rate limited.
            var now = _timeProvider.GetUtcNow();
            if (session.LastKeyRefreshAt is not { } last || now - last >= _options.WellKnownRefreshInterval)
            {
                session.LastKeyRefreshAt = now;
                try
                {
                    var wellKnown = await _relay.GetWellKnownAsync(session.Lifetime.Token).ConfigureAwait(false);
                    if (wellKnown.Socket is { } refreshed)
                        session.SocketInfo = refreshed;
                }
                catch (PushRelayApiException ex)
                {
                    _logger.Warning("The relay well-known document could not be refreshed for key {Kid}: {Code}.", kid, ex.Code);
                }

                relayKey = session.SocketInfo.Keys?.FirstOrDefault(k => k.Kid == kid);
            }

            if (relayKey is null)
                throw new RelaySocketCryptoException($"The relay key '{kid}' is not in the well-known document.");
        }

        var derived = RelaySocketPayloadCipher.DeriveKey(deviceKey.Pkcs8PrivateKey, relayKey, session.DeviceId);
        session.CacheDerivedKey(kid, derived);
        return derived;
    }

    private async Task<PushRelaySocketDeviceKey?> LoadDeviceKeyAsync(CancellationToken cancellationToken)
    {
        if (_keyStore is null)
            return null;

        try
        {
            return await _keyStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.Warning(ex, "The relay socket key could not be loaded or created.");
            return null;
        }
    }

    private static RelaySocketStartResult Classify(Exception error, PushRelayProblem? problem, bool timedOut)
    {
        if (error is PushRelayApiException relay)
            return new RelaySocketStartResult(relay.IsSignInRequired ? RelaySocketStartOutcome.SignInRequired : RelaySocketStartOutcome.Unavailable, relay.Problem, Error: relay);

        if (problem is null && error is HttpRequestException { StatusCode: { } status })
            problem = RelaySocketProblemReader.Parse(null, (int)status);

        if (timedOut)
            return new RelaySocketStartResult(RelaySocketStartOutcome.Unavailable, problem, Error: new TimeoutException("The relay socket did not connect in time.", error));

        if (problem is { Status: 401 or 403 or 429 })
            return new RelaySocketStartResult(RelaySocketStartOutcome.Refused, problem, Error: error);

        // No HTTP answer (DNS, TLS, a dropped WebSocket upgrade) or a server error: retried later.
        _ = error is WebSocketException;
        return new RelaySocketStartResult(RelaySocketStartOutcome.Unavailable, problem, Error: error);
    }

    private Uri ResolveHubUri(Uri relayBase, string? path)
    {
        var hubPath = string.IsNullOrWhiteSpace(path) ? RelaySocketProtocol.DefaultHubPath : path.Trim();
        return new Uri(relayBase, hubPath.TrimStart('/'));
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null) return;
        try
        {
            handler(this, args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Warning(ex, "A relay socket subscriber failed.");
        }
    }

    /// <summary>One hub connection attempt and what it reported.</summary>
    private sealed class Attempt(IRelayHubConnection connection, TaskCompletionSource<RelaySocketHello> hello)
    {
        public IRelayHubConnection Connection { get; } = connection;
        public TaskCompletionSource<RelaySocketHello> Hello { get; } = hello;
        public volatile bool HelloRaised;
        public volatile bool ReplayStarted;
        public volatile bool Closed;
        public string? ByeReason;
        public Exception? CloseError;
        public PushRelayProblem? CloseProblem;
    }

    /// <summary>Everything that lives from <c>StartAsync</c> to <c>StopAsync</c>: device, keys and the current attempt.</summary>
    private sealed class Session(Guid deviceId, PushRelaySocketDeviceKey? deviceKey, PushRelaySocketInfo socketInfo, Uri hubUri)
    {
        private readonly SemaphoreSlim _attemptLock = new(1, 1);
        private readonly Dictionary<string, byte[]> _derivedKeys = new(StringComparer.Ordinal);
        private Attempt? _attempt;

        public Guid DeviceId { get; } = deviceId;
        public PushRelaySocketDeviceKey? DeviceKey { get; } = deviceKey;
        public PushRelaySocketInfo SocketInfo { get; set; } = socketInfo;
        public Uri HubUri { get; } = hubUri;
        /// <summary>When the well-known document was last re-read because of an unknown key id; null before the first time.</summary>
        public DateTimeOffset? LastKeyRefreshAt { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public volatile bool Stopping;
        public int FailedAttempts;
        public TimeSpan? RetryAfter;
        public Task? ReconnectTask;

        public IRelayHubConnection? Connection => _attempt?.Connection;

        public bool IsCurrent(Attempt attempt) => ReferenceEquals(_attempt, attempt);

        /// <summary>Makes the attempt current unless the session is stopping; the previous attempt's connection is disposed.</summary>
        public async Task<bool> TryAdoptAsync(Attempt attempt)
        {
            await _attemptLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Stopping) return false;
                var previous = _attempt;
                _attempt = attempt;
                if (previous is not null)
                    await DisposeQuietlyAsync(previous.Connection).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _attemptLock.Release();
            }
        }

        /// <summary>Drops the attempt (closed or failed) and disposes its connection.</summary>
        public async Task ReleaseAsync(Attempt attempt)
        {
            await _attemptLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_attempt, attempt))
                    _attempt = null;
            }
            finally
            {
                _attemptLock.Release();
            }

            await DisposeQuietlyAsync(attempt.Connection).ConfigureAwait(false);
        }

        public async Task DisposeConnectionAsync()
        {
            Attempt? attempt;
            await _attemptLock.WaitAsync().ConfigureAwait(false);
            try
            {
                attempt = _attempt;
                _attempt = null;
            }
            finally
            {
                _attemptLock.Release();
            }

            if (attempt is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await attempt.Connection.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The connection may already be gone; disposal below is what matters.
                    _ = ex;
                }

                await DisposeQuietlyAsync(attempt.Connection).ConfigureAwait(false);
            }

            lock (_derivedKeys)
            {
                foreach (var key in _derivedKeys.Values)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                _derivedKeys.Clear();
            }
        }

        public bool TryGetDerivedKey(string kid, out byte[] key)
        {
            lock (_derivedKeys) return _derivedKeys.TryGetValue(kid, out key!);
        }

        public void CacheDerivedKey(string kid, byte[] key)
        {
            lock (_derivedKeys) _derivedKeys[kid] = key;
        }

        private static async Task DisposeQuietlyAsync(IRelayHubConnection connection)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug(ex, "A relay hub connection could not be disposed cleanly.");
            }
        }
    }
}
