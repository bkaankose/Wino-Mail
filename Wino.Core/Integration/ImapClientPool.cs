using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using MimeKit.Cryptography;
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
/// Provides an enhanced pooling mechanism for ImapClient with Channel-based async rental.
/// Maintains minimum active connections and a dedicated IDLE client.
/// </summary>
public class ImapClientPool : IDisposable
{
    private const int DefaultAcquireTimeoutMs = 45_000;
    private const int KeepAliveIntervalMs = 4 * 60 * 1000;
    private const int MaintenanceIntervalMs = 60 * 1000;

    private readonly ILogger _logger = Log.ForContext<ImapClientPool>();
    private readonly CustomServerInformation _customServerInformation;
    private readonly ConcurrentDictionary<WinoImapClient, ImapClientState> _clientStates = new();
    private readonly Channel<WinoImapClient> _availableClients;
    private readonly CancellationTokenSource _maintenanceCts = new();
    private readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private readonly object _idleClientLock = new();
    private readonly object _initialWarmupLock = new();
    private readonly ImapServerQuirkProfile _quirks;
    private readonly ImapImplementation _implementation;
    private readonly int _maxConnections;
    private readonly int _targetMinimumConnections;

    private DateTime _lastKeepAliveSentUtc = DateTime.MinValue;
    private Exception _lastConnectionException;
    private WinoImapClient _dedicatedIdleClient;
    private bool _disposedValue;
    private bool _initialized;
    private Task _maintenanceTask;
    private Task _initialWarmupTask = Task.CompletedTask;

    public bool ThrowOnSSLHandshakeCallback { get; set; }
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

        // Keep connection counts conservative by default and always cap by provider limits.
        _maxConnections = CalculateMaxConnections(_customServerInformation.MaxConcurrentClients);
        _targetMinimumConnections = CalculateTargetMinimumConnections(_maxConnections, _quirks.UseConservativeConnections);

        _implementation = CreateImplementation();

        CryptographyContext.Register(typeof(WindowsSecureMimeContext));

        _availableClients = Channel.CreateUnbounded<WinoImapClient>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>
    /// Initializes the pool by creating minimum connections and starting maintenance.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initializeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_initialized) return;

            _logger.Information("Initializing IMAP client pool with {MinimumConnections} minimum active connections (max: {MaxConnections})", _targetMinimumConnections, _maxConnections);

            // Fast-path startup: create one client eagerly so first RentAsync() is not blocked by full warm-up.
            var initialClient = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
            if (initialClient == null)
            {
                throw CreatePoolException("Failed to create initial IMAP connection for the pool.", _lastConnectionException);
            }

            _clientStates[initialClient] = ImapClientState.Available;
            await _availableClients.Writer.WriteAsync(initialClient, cancellationToken).ConfigureAwait(false);

            _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_maintenanceCts.Token), _maintenanceCts.Token);
            _initialized = true;

            ScheduleInitialWarmup();
            _logger.Information("IMAP client pool initialized. Health: {Health}", Health.Summary);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize IMAP client pool");
            throw CreatePoolException("IMAP client pool initialization failed.", ex);
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    /// <summary>
    /// Pre-warms the pool (legacy compatibility method).
    /// </summary>
    public async Task PreWarmPoolAsync()
    {
        await InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Task warmupTask;
        lock (_initialWarmupLock)
        {
            warmupTask = _initialWarmupTask;
        }

        if (warmupTask != null)
        {
            await warmupTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rents a client from the pool with the default timeout.
    /// </summary>
    public Task<WinoImapClient> RentAsync(CancellationToken cancellationToken = default)
        => RentAsync(TimeSpan.FromMilliseconds(DefaultAcquireTimeoutMs), cancellationToken);

    /// <summary>
    /// Rents a client from the pool with explicit timeout and cancellation.
    /// </summary>
    public async Task<WinoImapClient> RentAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);
        var token = linkedCts.Token;

        int createFailures = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_availableClients.Reader.TryRead(out var pooledClient))
                {
                    if (pooledClient != null && _clientStates.TryGetValue(pooledClient, out var state) && state == ImapClientState.Available)
                    {
                        try
                        {
                            await EnsureClientReadyAsync(pooledClient, token).ConfigureAwait(false);
                            _clientStates[pooledClient] = ImapClientState.InUse;
                            return pooledClient;
                        }
                        catch (Exception ex)
                        {
                            _lastConnectionException = ex;
                            _logger.Warning(ex, "Pooled IMAP client was not ready. Marking as failed.");
                            MarkClientAsFailed(pooledClient);
                        }
                    }
                }

                if (CanCreateAdditionalConnection())
                {
                    var newClient = await CreateAndConnectClientAsync(token).ConfigureAwait(false);
                    if (newClient != null)
                    {
                        _clientStates[newClient] = ImapClientState.InUse;
                        return newClient;
                    }

                    createFailures++;
                }

                await Task.Delay(150, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreatePoolException($"Timed out while acquiring an IMAP client after {timeout.TotalSeconds:F1} seconds. Failures: {createFailures}.", _lastConnectionException);
        }

        throw cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : CreatePoolException($"Failed to acquire IMAP client within {timeout.TotalSeconds:F1} seconds. Failures: {createFailures}.", _lastConnectionException);
    }

    /// <summary>
    /// Gets a client from the pool (legacy compatibility method).
    /// </summary>
    public Task<IImapClient> GetClientAsync()
        => GetClientAsync(CancellationToken.None, null);

    /// <summary>
    /// Gets a client from the pool with explicit cancellation and timeout control.
    /// </summary>
    public async Task<IImapClient> GetClientAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
        => await RentAsync(timeout ?? TimeSpan.FromMilliseconds(DefaultAcquireTimeoutMs), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Returns a client to the pool.
    /// </summary>
    public void Return(WinoImapClient client, bool isFaulted = false)
    {
        if (client == null || _disposedValue)
        {
            if (client != null)
                DisposeClient(client);
            return;
        }

        if (isFaulted || !client.IsConnected)
        {
            MarkClientAsFailed(client);
            return;
        }

        _clientStates[client] = ImapClientState.Available;
        _availableClients.Writer.TryWrite(client);
    }

    /// <summary>
    /// Releases a client (legacy compatibility method).
    /// </summary>
    public void Release(IImapClient item, bool destroyClient = false)
    {
        if (item is WinoImapClient winoClient)
        {
            Return(winoClient, destroyClient);
        }
        else if (item != null)
        {
            DisposeClient(item);
        }
    }

    /// <summary>
    /// Gets the dedicated IDLE client. Creates one if not available.
    /// </summary>
    public async Task<WinoImapClient> GetIdleClientAsync(CancellationToken cancellationToken = default)
    {
        lock (_idleClientLock)
        {
            if (_dedicatedIdleClient != null && _dedicatedIdleClient.IsConnected)
            {
                return _dedicatedIdleClient;
            }
        }

        if (!CanCreateAdditionalConnection())
        {
            _logger.Warning("Unable to allocate a dedicated IDLE client because pool is at max capacity ({MaxConnections}).", _maxConnections);
            return null;
        }

        var idleClient = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
        if (idleClient == null)
            return null;

        lock (_idleClientLock)
        {
            if (_dedicatedIdleClient != null)
            {
                MarkClientAsFailed(_dedicatedIdleClient);
            }

            _dedicatedIdleClient = idleClient;
            _clientStates[idleClient] = ImapClientState.Idle;
        }

        return idleClient;
    }

    /// <summary>
    /// Releases the IDLE client for reconnection.
    /// </summary>
    public void ReleaseIdleClient(bool isFaulted = false)
    {
        lock (_idleClientLock)
        {
            if (_dedicatedIdleClient == null)
                return;

            if (isFaulted || !_dedicatedIdleClient.IsConnected)
            {
                MarkClientAsFailed(_dedicatedIdleClient);
                _dedicatedIdleClient = null;
                return;
            }

            _clientStates[_dedicatedIdleClient] = ImapClientState.Idle;
        }
    }

    private ConnectionPoolHealth GetHealthInternal()
    {
        var health = new ConnectionPoolHealth
        {
            LastHealthCheck = DateTime.UtcNow,
            IdleConnectionActive = _dedicatedIdleClient?.IsConnected ?? false
        };

        foreach (var kvp in _clientStates)
        {
            health.TotalConnections++;
            switch (kvp.Value)
            {
                case ImapClientState.Available:
                    health.AvailableConnections++;
                    break;
                case ImapClientState.InUse:
                    health.InUseConnections++;
                    break;
                case ImapClientState.Failed:
                    health.FailedConnections++;
                    break;
                case ImapClientState.Reconnecting:
                    health.ReconnectingConnections++;
                    break;
            }
        }

        return health;
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MaintenanceIntervalMs, cancellationToken).ConfigureAwait(false);

                var keepAliveElapsedMs = (DateTime.UtcNow - _lastKeepAliveSentUtc).TotalMilliseconds;
                if (keepAliveElapsedMs >= KeepAliveIntervalMs)
                {
                    await SendNoOpToAvailableClientsAsync(cancellationToken).ConfigureAwait(false);
                    _lastKeepAliveSentUtc = DateTime.UtcNow;
                }

                await EnsureMinimumConnectionsAsync(cancellationToken).ConfigureAwait(false);
                await CleanupFailedConnectionsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error in pool maintenance loop");
            }
        }
    }

    private async Task SendNoOpToAvailableClientsAsync(CancellationToken cancellationToken)
    {
        foreach (var kvp in _clientStates)
        {
            if (kvp.Value != ImapClientState.Available)
                continue;

            if (!kvp.Key.IsConnected || kvp.Key.IsBusy())
                continue;

            try
            {
                await kvp.Key.NoOpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "NOOP failed for pooled client. Marking as failed.");
                MarkClientAsFailed(kvp.Key);
            }
        }
    }

    private async Task EnsureMinimumConnectionsAsync(CancellationToken cancellationToken)
    {
        var availableConnections = _clientStates.Count(kvp => kvp.Value == ImapClientState.Available);
        var neededConnections = _targetMinimumConnections - availableConnections;

        if (neededConnections <= 0)
            return;

        for (int i = 0; i < neededConnections; i++)
        {
            if (!CanCreateAdditionalConnection())
                break;

            try
            {
                var client = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
                if (client == null)
                    continue;

                _clientStates[client] = ImapClientState.Available;
                await _availableClients.Writer.WriteAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to create minimum pool connection during maintenance.");
                break;
            }
        }
    }

    private void ScheduleInitialWarmup()
    {
        lock (_initialWarmupLock)
        {
            if (_initialWarmupTask != null && !_initialWarmupTask.IsCompleted)
                return;

            _initialWarmupTask = Task.Run(() => EnsureWarmBaselineAsync(_maintenanceCts.Token), _maintenanceCts.Token);
        }
    }

    private async Task EnsureWarmBaselineAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureMinimumConnectionsAsync(cancellationToken).ConfigureAwait(false);

            lock (_idleClientLock)
            {
                if (_dedicatedIdleClient != null && _dedicatedIdleClient.IsConnected)
                    return;
            }

            if (!CanCreateAdditionalConnection())
                return;

            var idleCandidate = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
            if (idleCandidate == null)
                return;

            bool assignedAsIdle = false;
            lock (_idleClientLock)
            {
                if (_dedicatedIdleClient == null || !_dedicatedIdleClient.IsConnected)
                {
                    _dedicatedIdleClient = idleCandidate;
                    _clientStates[idleCandidate] = ImapClientState.Idle;
                    assignedAsIdle = true;
                }
            }

            if (!assignedAsIdle)
            {
                _clientStates[idleCandidate] = ImapClientState.Available;
                _availableClients.Writer.TryWrite(idleCandidate);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Pool is shutting down.
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Initial IMAP pool warm-up failed. Pool will continue with maintenance recovery.");
        }
    }

    private Task CleanupFailedConnectionsAsync()
    {
        foreach (var kvp in _clientStates)
        {
            if (kvp.Value != ImapClientState.Failed && kvp.Value != ImapClientState.Disposed)
                continue;

            DisposeClient(kvp.Key);
            _clientStates.TryRemove(kvp.Key, out _);
        }

        return Task.CompletedTask;
    }

    private async Task<WinoImapClient> CreateAndConnectClientAsync(CancellationToken cancellationToken)
    {
        var client = CreateNewClient();
        _lastConnectionException = null;

        try
        {
            await EnsureClientReadyAsync(client, cancellationToken).ConfigureAwait(false);
            _lastConnectionException = null;
            return client;
        }
        catch (Exception ex)
        {
            _lastConnectionException = ex;
            _logger.Warning(ex, "Failed to create and connect IMAP client.");
            DisposeClient(client);
            return null;
        }
    }

    private async Task EnsureClientReadyAsync(WinoImapClient client, CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            client.ServerCertificateValidationCallback = MyServerCertificateValidationCallback;

            await client.ConnectAsync(
                _customServerInformation.IncomingServer,
                int.Parse(_customServerInformation.IncomingServerPort),
                GetSocketOptions(_customServerInformation.IncomingServerSocketOption),
                cancellationToken).ConfigureAwait(false);

            if (client.Capabilities.HasFlag(ImapCapabilities.Compress))
            {
                try
                {
                    await client.CompressAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to enable IMAP compression. Continuing without compression.");
                }
            }

            await TryIdentifyAsync(client, cancellationToken).ConfigureAwait(false);
        }

        if (!client.IsAuthenticated)
        {
            var cred = new NetworkCredential(
                _customServerInformation.IncomingServerUsername,
                _customServerInformation.IncomingServerPassword);

            var authMethod = _customServerInformation.IncomingAuthenticationMethod;

            if (authMethod != ImapAuthenticationMethod.Auto)
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
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to enable QRESYNC for {Server}. Falling back to non-QRESYNC synchronization.", _customServerInformation.IncomingServer);
                }
            }
        }
    }

    private async Task TryIdentifyAsync(WinoImapClient client, CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Id))
            return;

        try
        {
            await client.IdentifyAsync(_implementation, cancellationToken).ConfigureAwait(false);
        }
        catch (ImapCommandException)
        {
            // Some servers refuse ID even if advertised. Ignore and continue.
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to send IMAP ID payload. Continuing without Identify().");
        }
    }

    private WinoImapClient CreateNewClient()
    {
        var client = new WinoImapClient();

        if (!string.IsNullOrEmpty(_customServerInformation.ProxyServer))
        {
            client.ProxyClient = new HttpProxyClient(
                _customServerInformation.ProxyServer,
                int.Parse(_customServerInformation.ProxyServerPort));
        }

        _logger.Debug("Created new IMAP client. Current tracked pool size: {Count}", _clientStates.Count);
        return client;
    }

    private void DisposeClient(IImapClient client)
    {
        if (client == null)
            return;

        try
        {
            if (client.IsConnected)
            {
                lock (client.SyncRoot)
                {
                    client.Disconnect(quit: true);
                }
            }

            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error disposing IMAP client.");
        }
    }

    private void MarkClientAsFailed(WinoImapClient client)
    {
        if (client == null)
            return;

        _clientStates[client] = ImapClientState.Failed;
    }

    private bool CanCreateAdditionalConnection()
    {
        var activeCount = _clientStates.Count(kvp => kvp.Value != ImapClientState.Failed && kvp.Value != ImapClientState.Disposed);
        return activeCount < _maxConnections;
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
        ImapConnectionSecurity.StartTls => SecureSocketOptions.StartTlsWhenAvailable,
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

    private bool MyServerCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None) return true;

        if (ThrowOnSSLHandshakeCallback)
        {
            throw new ImapTestSSLCertificateException(
                certificate.Issuer,
                certificate.GetExpirationDateString(),
                certificate.GetEffectiveDateString());
        }

        return true;
    }

    // Legacy compatibility methods
    public Task<bool> EnsureConnectedAsync(IImapClient client) =>
        Task.FromResult(client.IsConnected);

    public Task EnsureAuthenticatedAsync(IImapClient client) =>
        Task.CompletedTask;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        if (disposing)
        {
            _maintenanceCts.Cancel();
            _maintenanceTask?.Wait(TimeSpan.FromSeconds(5));
            _maintenanceCts.Dispose();
            _initializeSemaphore.Dispose();

            _availableClients.Writer.Complete();

            foreach (var kvp in _clientStates)
            {
                DisposeClient(kvp.Key);
            }

            _clientStates.Clear();

            lock (_idleClientLock)
            {
                _dedicatedIdleClient = null;
            }

        }

        _disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
