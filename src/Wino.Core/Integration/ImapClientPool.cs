using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Integration;

/// <summary>
/// Connection state for tracking individual client health.
/// </summary>
public enum ImapClientState
{
    Available,
    InUse,
    Idle,
    Reconnecting,
    Failed,
    Disposed
}

/// <summary>
/// Provides exclusive leases for IMAP operations and maintenance.
/// Maintains minimum active connections and a dedicated IDLE client.
/// </summary>
public class ImapClientPool : IDisposable, IAsyncDisposable
{
    private const int DefaultAcquireTimeoutMs = 45_000;
    internal static readonly TimeSpan NoOpTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = Log.ForContext<ImapClientPool>();
    private readonly CustomServerInformation _customServerInformation;
    private readonly ConcurrentDictionary<WinoImapClient, ImapClientState> _clientStates = new();
    private readonly SemaphoreSlim _creationSemaphore = new(1, 1);
    private readonly object _disposeLock = new();
    private readonly CancellationTokenSource _maintenanceCts = new();
    private readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private readonly object _idleClientLock = new();
    private readonly object _initialWarmupLock = new();
    private readonly ImapServerQuirkProfile _quirks;
    private readonly ImapImplementation _implementation;
    private readonly int _maxConnections;
    private readonly int _targetMinimumConnections;

    private bool _supportsIdle;
    private Task _disposeTask;
    private WinoImapClient _dedicatedIdleClient;
    private volatile bool _disposedValue;
    private bool _initialized;
    private Task _maintenanceTask;
    private Task _initialWarmupTask = Task.CompletedTask;

    public ImapClientPoolOptions ImapClientPoolOptions { get; }

    /// <summary>
    /// Gets the current health status of the connection pool.
    /// </summary>
    public ConnectionPoolHealth Health => GetHealthInternal();

    public ImapClientPool(ImapClientPoolOptions imapClientPoolOptions)
    {
        _customServerInformation = imapClientPoolOptions.ServerInformation;
        ImapClientPoolOptions = imapClientPoolOptions;

        _quirks = ImapServerQuirks.Resolve(_customServerInformation.IncomingServer);

        // Honor the configured maximum, including connections still being established.
        _maxConnections = CalculateMaxConnections(_customServerInformation.MaxConcurrentClients);
        _targetMinimumConnections = CalculateTargetMinimumConnections(_maxConnections, _quirks.UseConservativeConnections);

        _implementation = CreateImplementation();
    }

    public bool SupportsIdle => _supportsIdle && _maxConnections > 1 && !ImapClientPoolOptions.IsTestPool;

    // Available -> InUse is the lease. Maintenance uses the same transition as borrowers.
    // A client stays owned until the caller's entire mailbox operation has finished.
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCts.Token);
        deadline.CancelAfter(DefaultAcquireTimeoutMs);
        await _initializeSemaphore.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var client = await CreateAndConnectClientAsync(deadline.Token, "Initialize").ConfigureAwait(false)
                ?? throw CreatePoolException("No capacity for the initial IMAP connection.");
            _supportsIdle = client.Capabilities.HasFlag(ImapCapabilities.Idle);
            Return(client);
            _initialized = true;
            if (!ImapClientPoolOptions.IsTestPool)
                _maintenanceTask = MaintenanceLoopAsync(_maintenanceCts.Token);
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    public async Task PreWarmPoolAsync()
    {
        try
        {
            await InitializeAsync(_maintenanceCts.Token).ConfigureAwait(false);
            if (ImapClientPoolOptions.IsTestPool) return;
            Task warmup;
            lock (_initialWarmupLock)
            {
                if (_initialWarmupTask.IsCompleted)
                    _initialWarmupTask = EnsureMinimumConnectionsAsync(_maintenanceCts.Token);
                warmup = _initialWarmupTask;
            }
            await warmup.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_maintenanceCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IMAP pool warm-up failed for {Server}.", _customServerInformation.IncomingServer);
        }
    }

    public Task<WinoImapClient> RentAsync(CancellationToken cancellationToken = default)
        => RentAsync(TimeSpan.FromMilliseconds(DefaultAcquireTimeoutMs), cancellationToken);

    // The purpose only names the requester in the connection log so a pool that grows can be explained.
    public async Task<WinoImapClient> RentAsync(TimeSpan timeout, CancellationToken cancellationToken = default, string purpose = null)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCts.Token);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            await InitializeAsync(token).ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposedValue, this);
                foreach (var candidate in _clientStates)
                {
                    if (!_clientStates.TryUpdate(candidate.Key, ImapClientState.InUse, ImapClientState.Available))
                        continue;
                    var client = candidate.Key;
                    try
                    {
                        if (!client.IsConnected)
                            throw new ServiceNotConnectedException("The pooled IMAP connection is closed.");
                        if (DateTime.UtcNow - client.LastUsedUtc >= _quirks.KeepAliveInterval)
                            await NoOpAsync(client, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        return client;
                    }
                    catch (Exception ex) when (IsConnectionFailure(ex) || ex is OperationCanceledException)
                    {
                        Retire(client);
                        token.ThrowIfCancellationRequested();
                        _logger.Debug(ex, "Discarded stale IMAP connection {ConnectionId}.", client.ConnectionId);
                    }
                    catch
                    {
                        Retire(client);
                        throw;
                    }
                }
                var created = await CreateAndConnectClientAsync(token, purpose).ConfigureAwait(false);
                if (created != null) return created;
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_maintenanceCts.IsCancellationRequested)
        {
            throw CreatePoolException($"Timed out while acquiring an IMAP client after {timeout.TotalSeconds:F1} seconds.");
        }
    }

    public Task<IImapClient> GetClientAsync() => GetClientAsync(CancellationToken.None);
    public async Task<IImapClient> GetClientAsync(CancellationToken cancellationToken, TimeSpan? timeout = null, string purpose = null)
        => await RentAsync(timeout ?? TimeSpan.FromMilliseconds(DefaultAcquireTimeoutMs), cancellationToken, purpose).ConfigureAwait(false);

    internal Task<IImapClient> RentForReadAsync(CancellationToken cancellationToken, bool allowCreate = true, string purpose = null)
        => RentForReadAsync(NoOpIfStaleAsync, cancellationToken, allowCreate, purpose);

    // A connection that was used inside the keep-alive window was already validated by the rent path.
    // A second NOOP on it is a wasted round trip on every folder sync.
    private Task NoOpIfStaleAsync(IImapClient client, CancellationToken cancellationToken)
    {
        var winoClient = (WinoImapClient)client;
        return DateTime.UtcNow - winoClient.LastUsedUtc < _quirks.KeepAliveInterval
            ? Task.CompletedTask
            : NoOpAsync(winoClient, cancellationToken);
    }

    /// <summary>
    /// Leases an already-connected idle client without opening a new socket.
    /// Returns null when no validated spare connection exists.
    /// </summary>
    public async Task<WinoImapClient> TryRentAvailableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);
        if (!_initialized) return null;
        foreach (var candidate in _clientStates)
        {
            if (!_clientStates.TryUpdate(candidate.Key, ImapClientState.InUse, ImapClientState.Available))
                continue;
            var client = candidate.Key;
            try
            {
                if (!client.IsConnected)
                    throw new ServiceNotConnectedException("The pooled IMAP connection is closed.");
                if (DateTime.UtcNow - client.LastUsedUtc >= _quirks.KeepAliveInterval)
                    await NoOpAsync(client, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return client;
            }
            catch (Exception ex) when (IsConnectionFailure(ex) || ex is OperationCanceledException)
            {
                Retire(client);
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Debug(ex, "Discarded stale IMAP connection {ConnectionId}.", client.ConnectionId);
            }
            catch
            {
                Retire(client);
                throw;
            }
        }
        return null;
    }

    // Only preparation reads may be replayed. The lease is returned to the caller before
    // local sync state or remote mutations are changed.
    // With allowCreate false, only a spare connection is leased and null means none exists.
    internal async Task<IImapClient> RentForReadAsync(
        Func<IImapClient, CancellationToken, Task> prepare, CancellationToken cancellationToken, bool allowCreate = true, string purpose = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCts.Token);
        deadline.CancelAfter(DefaultAcquireTimeoutMs);
        var callerToken = cancellationToken;
        cancellationToken = deadline.Token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                IImapClient client;
                if (!allowCreate)
                {
                    client = await TryRentAvailableAsync(cancellationToken).ConfigureAwait(false);
                    if (client == null) return null;
                }
                else
                {
                    client = attempt == 0
                        ? await GetClientAsync(cancellationToken, purpose: purpose).ConfigureAwait(false)
                        : await RentFreshAsync(cancellationToken, purpose).ConfigureAwait(false);
                }
                try
                {
                    await prepare(client, cancellationToken).ConfigureAwait(false);
                    return client;
                }
                catch (Exception ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested && IsConnectionFailure(ex))
                {
                    Release(client, destroyClient: true);
                    _logger.Information("Retrying IMAP read on a fresh connection for {Server}.", _customServerInformation.IncomingServer);
                }
                catch
                {
                    Release(client, destroyClient: true);
                    throw;
                }
            }
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested && !_maintenanceCts.IsCancellationRequested)
        {
            throw new IOException("Timed out preparing the IMAP read connection.", ex);
        }
    }

    private async Task<IImapClient> RentFreshAsync(CancellationToken cancellationToken, string purpose)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCts.Token);
        deadline.CancelAfter(DefaultAcquireTimeoutMs);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            // Replace an unused socket if another borrower filled the released slot.
            if (_clientStates.Count >= _maxConnections)
            {
                foreach (var entry in _clientStates)
                    if (_clientStates.TryUpdate(entry.Key, ImapClientState.InUse, ImapClientState.Available))
                    {
                        Retire(entry.Key);
                        break;
                    }
            }
            var client = await CreateAndConnectClientAsync(deadline.Token, purpose).ConfigureAwait(false);
            if (client != null) return client;
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    internal static bool IsConnectionFailure(Exception exception)
        => exception is IOException or SocketException or ServiceNotConnectedException or ImapProtocolException;

    public void Return(WinoImapClient client, bool isFaulted = false)
    {
        if (client == null) return;
        if (_disposedValue || isFaulted || !client.IsConnected)
        {
            Retire(client);
            return;
        }
        client.LastUsedUtc = DateTime.UtcNow;
        _clientStates.TryUpdate(client, ImapClientState.Available, ImapClientState.InUse);
    }

    public void Release(IImapClient item, bool destroyClient = false)
    {
        if (item is WinoImapClient client) Return(client, destroyClient);
        else item?.Dispose();
    }

    public async Task<WinoImapClient> GetIdleClientAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!SupportsIdle) return null;
        lock (_idleClientLock)
        {
            if (_dedicatedIdleClient != null)
                throw new InvalidOperationException("The dedicated IMAP IDLE client already has an owner.");
        }
        WinoImapClient client = null;
        foreach (var entry in _clientStates)
            if (_clientStates.TryUpdate(entry.Key, ImapClientState.InUse, ImapClientState.Available))
            {
                client = entry.Key;
                break;
            }
        client ??= await CreateAndConnectClientAsync(cancellationToken, "Idle").ConfigureAwait(false);
        if (client == null) return null;
        lock (_idleClientLock)
        {
            if (_disposedValue || _dedicatedIdleClient != null)
            {
                Retire(client);
                return null;
            }
            _dedicatedIdleClient = client;
            _clientStates[client] = ImapClientState.Idle;
        }
        return client;
    }

    public void ReleaseIdleClient(bool isFaulted = false)
    {
        lock (_idleClientLock)
        {
            if (_dedicatedIdleClient == null) return;
            Retire(_dedicatedIdleClient);
            _dedicatedIdleClient = null;
        }
    }

    private ConnectionPoolHealth GetHealthInternal()
    {
        var health = new ConnectionPoolHealth
        {
            LastHealthCheck = DateTime.UtcNow,
            IdleConnectionActive = _dedicatedIdleClient?.IsIdle == true
        };
        foreach (var entry in _clientStates)
        {
            health.TotalConnections++;
            switch (entry.Value)
            {
                case ImapClientState.Available: health.AvailableConnections++; break;
                case ImapClientState.InUse: health.InUseConnections++; break;
                case ImapClientState.Failed: health.FailedConnections++; break;
                case ImapClientState.Reconnecting: health.ReconnectingConnections++; break;
            }
        }
        return health;
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_quirks.KeepAliveInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendNoOpToAvailableClientsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Sends a NOOP bounded by the pool's keepalive timeout. Usable as a read preparation step.
    /// </summary>
    internal Task ValidateAsync(IImapClient client, CancellationToken cancellationToken)
        => NoOpAsync((WinoImapClient)client, cancellationToken);

    private async Task NoOpAsync(WinoImapClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NoOpTimeout);
        try
        {
            await client.NoOpAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("IMAP NOOP timed out.", ex);
        }
        client.LastUsedUtc = DateTime.UtcNow;
        _logger.Verbose("IMAP keepalive succeeded for {Server}, connection {ConnectionId}.",
            _customServerInformation.IncomingServer, client.ConnectionId);
    }

    internal Task SendNoOpToAvailableClientsAsync(CancellationToken cancellationToken)
        => Task.WhenAll(_clientStates.Keys.Select(async client =>
        {
            if (!_clientStates.TryUpdate(client, ImapClientState.InUse, ImapClientState.Available)) return;
            try
            {
                await NoOpAsync(client, cancellationToken).ConfigureAwait(false);
                Return(client);
            }
            catch (Exception ex)
            {
                Retire(client);
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Debug(ex, "IMAP keepalive failed for connection {ConnectionId}.", client.ConnectionId);
            }
        }));

    private async Task EnsureMinimumConnectionsAsync(CancellationToken cancellationToken)
    {
        while (_clientStates.Count < _targetMinimumConnections)
        {
            var client = await CreateAndConnectClientAsync(cancellationToken, "WarmUp").ConfigureAwait(false);
            if (client == null) return;
            Return(client);
        }
    }

    private async Task<WinoImapClient> CreateAndConnectClientAsync(CancellationToken cancellationToken, string purpose = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCts.Token);
        timeout.CancelAfter(DefaultAcquireTimeoutMs);
        await _creationSemaphore.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposedValue, this);
            if (_clientStates.Count >= _maxConnections) return null;
            var client = CreateNewClient(purpose);
            _clientStates[client] = ImapClientState.Reconnecting;
            try
            {
                await EnsureClientReadyAsync(client, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposedValue, this);
                client.LastUsedUtc = DateTime.UtcNow;
                _clientStates[client] = ImapClientState.InUse;
                return client;
            }
            catch
            {
                Retire(client);
                throw;
            }
        }
        finally
        {
            _creationSemaphore.Release();
        }
    }

    private async Task EnsureClientReadyAsync(WinoImapClient client, CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            var host = _customServerInformation.IncomingServer;
            var port = int.Parse(_customServerInformation.IncomingServerPort);
            var storedTrust = ImapClientPoolOptions.CertificateTrustService == null
                ? null
                : await ImapClientPoolOptions.CertificateTrustService
                    .GetTrustAsync(_customServerInformation.AccountId, MailServerProtocol.Imap, host, port)
                    .ConfigureAwait(false);
            var transientTrust = _customServerInformation.PendingCertificateTrusts?
                .LastOrDefault(item => item.Protocol == MailServerProtocol.Imap &&
                                       string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                       item.Port == port);

            client.ServerCertificateValidationCallback = (_, certificate, chain, sslPolicyErrors)
                => MailKitServerCertificateValidator.Validate(
                    certificate, chain, sslPolicyErrors, MailServerProtocol.Imap, host, port, storedTrust, transientTrust);

            await client.ConnectAsync(
                host,
                port,
                GetSocketOptions(_customServerInformation.IncomingServerSocketOption),
                cancellationToken).ConfigureAwait(false);

            if (client.Capabilities.HasFlag(ImapCapabilities.Compress))
            {
                try
                {
                    await client.CompressAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ImapCommandException ex) when (client.IsConnected)
                {
                    _logger.Debug(ex, "Failed to enable IMAP compression. Continuing without compression.");
                }
            }

            await TryIdentifyAsync(client, cancellationToken).ConfigureAwait(false);
        }

        var configuredAuthMethod = _customServerInformation.IncomingAuthenticationMethod;
        var correctedPolicy = _customServerInformation.ConnectionPolicyVersion == ImapConnectionPolicyVersion.Corrected;

        if (!client.IsAuthenticated && !(correctedPolicy && configuredAuthMethod == ImapAuthenticationMethod.None))
        {
            var authMethod = configuredAuthMethod;

            var cred = new NetworkCredential(
                _customServerInformation.IncomingServerUsername,
                _customServerInformation.IncomingServerPassword);

            if (correctedPolicy && authMethod is ImapAuthenticationMethod.NormalPassword or ImapAuthenticationMethod.Auto)
            {
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
                await client.AuthenticateAsync(cred, cancellationToken).ConfigureAwait(false);
            }
            else if (authMethod != ImapAuthenticationMethod.Auto)
            {
                client.AuthenticationMechanisms.Clear();
                var saslMechanism = GetSASLAuthenticationMethodName(authMethod);
                client.AuthenticationMechanisms.Add(saslMechanism);
                await client.AuthenticateAsync(SaslMechanism.Create(saslMechanism, cred), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(cred, cancellationToken).ConfigureAwait(false);
            }

            await TryIdentifyAsync(client, cancellationToken).ConfigureAwait(false);

            client.IsQResyncEnabled = false;
            if (!_quirks.DisableQResync && client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
            {
                try
                {
                    await client.EnableQuickResyncAsync(cancellationToken).ConfigureAwait(false);
                    client.IsQResyncEnabled = true;
                }
                catch (ImapCommandException ex) when (client.IsConnected)
                {
                    _logger.Debug(ex, "Failed to enable QRESYNC for {Server}. Falling back to non-QRESYNC synchronization.", _customServerInformation.IncomingServer);
                }
            }
        }
        if (!client.IsConnected)
            throw new ServiceNotConnectedException("The IMAP connection closed during negotiation.");
    }

    private async Task TryIdentifyAsync(WinoImapClient client, CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Id))
            return;

        try
        {
            await client.IdentifyAsync(_implementation, cancellationToken).ConfigureAwait(false);
        }
        catch (ImapCommandException) when (client.IsConnected)
        {
            // Some servers refuse ID even if advertised. Ignore and continue.
        }
    }

    private WinoImapClient CreateNewClient(string purpose)
    {
        var client = ImapClientPoolOptions.ProtocolLoggerFactory == null
            ? new WinoImapClient()
            : new WinoImapClient(ImapClientPoolOptions.ProtocolLoggerFactory());

        if (!string.IsNullOrEmpty(_customServerInformation.ProxyServer))
        {
            client.ProxyClient = new HttpProxyClient(
                _customServerInformation.ProxyServer,
                int.Parse(_customServerInformation.ProxyServerPort));
        }

        _logger.Debug("Created IMAP connection {ConnectionId} for {Server} ({Purpose}). Tracked pool size: {Count}.",
            client.ConnectionId, _customServerInformation.IncomingServer, purpose ?? "unspecified", _clientStates.Count);
        return client;
    }

    private void Retire(WinoImapClient client)
    {
        if (!_clientStates.TryGetValue(client, out var state) || state == ImapClientState.Disposed ||
            !_clientStates.TryUpdate(client, ImapClientState.Disposed, state)) return;
        try
        {
            // Do not enqueue LOGOUT on a faulted or canceled connection.
            client.Dispose();
            _logger.Debug("Retired IMAP connection {ConnectionId} for {Server}.", client.ConnectionId, _customServerInformation.IncomingServer);
        }
        finally
        {
            _clientStates.TryRemove(client, out _);
        }
    }

    private ImapClientPoolException CreatePoolException(string message, Exception innerException = null)
    {
        return innerException == null
            ? new ImapClientPoolException(message, _customServerInformation)
            : new ImapClientPoolException(innerException);
    }

    private static ImapImplementation CreateImplementation()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        return new ImapImplementation
        {
            Name = "Wino Mail",
            Version = version,
            Vendor = "Wino",
            OS = Environment.OSVersion.VersionString,
            SupportUrl = "https://www.winomail.app"
        };
    }

    public static int CalculateMaxConnections(int configuredMaxConcurrentClients)
        => Math.Clamp(configuredMaxConcurrentClients <= 0 ? 5 : configuredMaxConcurrentClients, 1, 10);

    public static int CalculateTargetMinimumConnections(int maxConnections, bool useConservativeConnections)
        => useConservativeConnections ? 1 : Math.Min(2, Math.Max(1, maxConnections));

    private SecureSocketOptions GetSocketOptions(ImapConnectionSecurity connectionSecurity) => connectionSecurity switch
    {
        ImapConnectionSecurity.Auto => SecureSocketOptions.Auto,
        ImapConnectionSecurity.None => SecureSocketOptions.None,
        ImapConnectionSecurity.StartTls => _customServerInformation.ConnectionPolicyVersion == ImapConnectionPolicyVersion.Corrected
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.StartTlsWhenAvailable,
        ImapConnectionSecurity.SslTls => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None
    };

    private string GetSASLAuthenticationMethodName(ImapAuthenticationMethod method) => method switch
    {
        ImapAuthenticationMethod.NormalPassword => "PLAIN",
        ImapAuthenticationMethod.EncryptedPassword => "LOGIN",
        ImapAuthenticationMethod.Ntlm => "NTLM",
        ImapAuthenticationMethod.CramMd5 => "CRAM-MD5",
        ImapAuthenticationMethod.DigestMd5 => "DIGEST-MD5",
        _ => "PLAIN"
    };

    // Legacy compatibility methods
    public Task<bool> EnsureConnectedAsync(IImapClient client) =>
        Task.FromResult(client.IsConnected);

    public Task EnsureAuthenticatedAsync(IImapClient client) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask == null)
            {
                _disposedValue = true;
                _maintenanceCts.Cancel();
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        try
        {
            await Task.WhenAll(_maintenanceTask ?? Task.CompletedTask, _initialWarmupTask)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        finally
        {
            while (_clientStates.Any(entry => entry.Value is ImapClientState.InUse or ImapClientState.Reconnecting or ImapClientState.Idle)
                   && DateTime.UtcNow < deadline)
                await Task.Delay(25).ConfigureAwait(false);
            foreach (var client in _clientStates.Keys) Retire(client);
            lock (_idleClientLock) _dedicatedIdleClient = null;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
