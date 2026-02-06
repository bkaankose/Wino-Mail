using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
    private const int MinActiveConnections = 3;
    private const int IdleConnectionReserved = 1;
    private const int KeepAliveIntervalMs = 4 * 60 * 1000; // 4 minutes
    private const int ConnectionMonitorIntervalMs = 30 * 1000; // 30 seconds
    private const int MaintenanceIntervalMs = 60 * 1000; // 1 minute

    private readonly ImapImplementation _implementation = new()
    {
        Version = "1.8.0",
        OS = "Windows",
        Vendor = "Wino",
        SupportUrl = "https://www.winomail.app",
        Name = "Wino Mail User",
    };

    private readonly ILogger _logger = Log.ForContext<ImapClientPool>();
    private readonly CustomServerInformation _customServerInformation;
    private readonly Stream _protocolLogStream;
    private readonly ConcurrentDictionary<WinoImapClient, ImapClientState> _clientStates = new();
    private readonly Channel<WinoImapClient> _availableClients;
    private readonly CancellationTokenSource _maintenanceCts = new();
    private readonly object _idleClientLock = new();

    private WinoImapClient _dedicatedIdleClient;
    private bool _disposedValue;
    private bool _initialized;
    private Task _maintenanceTask;

    public bool ThrowOnSSLHandshakeCallback { get; set; }
    public ImapClientPoolOptions ImapClientPoolOptions { get; }

    /// <summary>
    /// Gets the current health status of the connection pool.
    /// </summary>
    public ConnectionPoolHealth Health => GetHealthInternal();

    public ImapClientPool(ImapClientPoolOptions imapClientPoolOptions)
    {
        _customServerInformation = imapClientPoolOptions.ServerInformation;
        _protocolLogStream = imapClientPoolOptions.ProtocolLog;
        ImapClientPoolOptions = imapClientPoolOptions;

        CryptographyContext.Register(typeof(WindowsSecureMimeContext));

        // Create unbounded channel for available clients
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

        _logger.Information("Initializing IMAP client pool with {MinConnections} connections", MinActiveConnections);

        try
        {
            // Create initial connections
            for (int i = 0; i < MinActiveConnections; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var client = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
                if (client != null)
                {
                    _clientStates[client] = ImapClientState.Available;
                    await _availableClients.Writer.WriteAsync(client, cancellationToken).ConfigureAwait(false);
                }
            }

            // Create dedicated IDLE client
            _dedicatedIdleClient = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
            if (_dedicatedIdleClient != null)
            {
                _clientStates[_dedicatedIdleClient] = ImapClientState.Idle;
            }

            // Start maintenance task
            _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_maintenanceCts.Token), _maintenanceCts.Token);

            _initialized = true;
            _logger.Information("IMAP client pool initialized. Health: {Health}", Health.Summary);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize IMAP client pool");
            throw;
        }
    }

    /// <summary>
    /// Pre-warms the pool (legacy compatibility method).
    /// </summary>
    public Task PreWarmPoolAsync() => InitializeAsync(CancellationToken.None);

    /// <summary>
    /// Rents a client from the pool. Blocks until a client is available.
    /// </summary>
    public async Task<WinoImapClient> RentAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Try to get an available client from the channel
            if (_availableClients.Reader.TryRead(out var client))
            {
                if (client != null && _clientStates.TryGetValue(client, out var state) && state == ImapClientState.Available)
                {
                    try
                    {
                        // Ensure client is still connected
                        await EnsureClientReadyAsync(client, cancellationToken).ConfigureAwait(false);
                        _clientStates[client] = ImapClientState.InUse;
                        return client;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Client from pool was not ready, marking as failed");
                        _clientStates[client] = ImapClientState.Failed;
                        // Continue to try next client or create new one
                    }
                }
            }

            // No available client, try to create a new one
            var newClient = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
            if (newClient != null)
            {
                _clientStates[newClient] = ImapClientState.InUse;
                return newClient;
            }

            // Wait a bit before retrying
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    /// Gets a client from the pool (legacy compatibility method).
    /// </summary>
    public async Task<IImapClient> GetClientAsync() => await RentAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Returns a client to the pool.
    /// </summary>
    public void Return(WinoImapClient client, bool isFaulted = false)
    {
        if (client == null) return;

        if (isFaulted || !client.IsConnected)
        {
            _clientStates[client] = ImapClientState.Failed;
            DisposeClient(client);
            return;
        }

        if (!_disposedValue)
        {
            _clientStates[client] = ImapClientState.Available;
            _availableClients.Writer.TryWrite(client);
        }
        else
        {
            DisposeClient(client);
        }
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

        // Need to create or reconnect IDLE client
        var idleClient = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);

        lock (_idleClientLock)
        {
            if (_dedicatedIdleClient != null)
            {
                DisposeClient(_dedicatedIdleClient);
            }
            _dedicatedIdleClient = idleClient;
            if (idleClient != null)
            {
                _clientStates[idleClient] = ImapClientState.Idle;
            }
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
            if (_dedicatedIdleClient != null)
            {
                if (isFaulted)
                {
                    _clientStates[_dedicatedIdleClient] = ImapClientState.Failed;
                    DisposeClient(_dedicatedIdleClient);
                    _dedicatedIdleClient = null;
                }
                else
                {
                    _clientStates[_dedicatedIdleClient] = ImapClientState.Idle;
                }
            }
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

                // Send NOOP to keep connections alive
                await SendNoOpToAvailableClientsAsync(cancellationToken).ConfigureAwait(false);

                // Ensure minimum connections
                await EnsureMinimumConnectionsAsync(cancellationToken).ConfigureAwait(false);

                // Clean up failed connections
                await CleanupFailedConnectionsAsync(cancellationToken).ConfigureAwait(false);
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
            if (kvp.Value == ImapClientState.Available && kvp.Key.IsConnected && !kvp.Key.IsBusy())
            {
                try
                {
                    await kvp.Key.NoOpAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "NOOP failed for client, marking as failed");
                    _clientStates[kvp.Key] = ImapClientState.Failed;
                }
            }
        }
    }

    private async Task EnsureMinimumConnectionsAsync(CancellationToken cancellationToken)
    {
        var health = Health;
        var neededConnections = MinActiveConnections - health.AvailableConnections;

        if (neededConnections > 0)
        {
            _logger.Debug("Creating {Count} connections to maintain minimum pool size", neededConnections);

            for (int i = 0; i < neededConnections; i++)
            {
                try
                {
                    var client = await CreateAndConnectClientAsync(cancellationToken).ConfigureAwait(false);
                    if (client != null)
                    {
                        _clientStates[client] = ImapClientState.Available;
                        await _availableClients.Writer.WriteAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to create new connection during maintenance");
                }
            }
        }
    }

    private Task CleanupFailedConnectionsAsync(CancellationToken cancellationToken)
    {
        foreach (var kvp in _clientStates)
        {
            if (kvp.Value == ImapClientState.Failed)
            {
                DisposeClient(kvp.Key);
                _clientStates.TryRemove(kvp.Key, out _);
            }
        }
        return Task.CompletedTask;
    }

    private async Task<WinoImapClient> CreateAndConnectClientAsync(CancellationToken cancellationToken)
    {
        var client = CreateNewClient();

        try
        {
            await EnsureClientReadyAsync(client, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to create and connect new client");
            DisposeClient(client);
            return null;
        }
    }

    private async Task EnsureClientReadyAsync(WinoImapClient client, CancellationToken cancellationToken)
    {
        // Connect if needed
        if (!client.IsConnected)
        {
            client.ServerCertificateValidationCallback = MyServerCertificateValidationCallback;

            await client.ConnectAsync(
                _customServerInformation.IncomingServer,
                int.Parse(_customServerInformation.IncomingServerPort),
                GetSocketOptions(_customServerInformation.IncomingServerSocketOption),
                cancellationToken).ConfigureAwait(false);

            // Enable compression if supported
            if (client.Capabilities.HasFlag(ImapCapabilities.Compress))
            {
                await client.CompressAsync(cancellationToken).ConfigureAwait(false);
            }

            // Handle ID extension
            if (client.Capabilities.HasFlag(ImapCapabilities.Id))
            {
                try
                {
                    await client.IdentifyAsync(_implementation, cancellationToken).ConfigureAwait(false);
                }
                catch (ImapCommandException)
                {
                    // Some servers require post-auth identification
                }
            }
        }

        // Authenticate if needed
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

            // Try post-auth ID if needed
            if (client.Capabilities.HasFlag(ImapCapabilities.Id))
            {
                try
                {
                    await client.IdentifyAsync(_implementation, cancellationToken).ConfigureAwait(false);
                }
                catch { /* Ignore */ }
            }

            // Enable QRESYNC if supported
            if (client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
            {
                await client.EnableQuickResyncAsync(cancellationToken).ConfigureAwait(false);
                client.IsQResyncEnabled = true;
            }
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

        _logger.Debug("Created new ImapClient. Current pool size: {Count}", _clientStates.Count);
        return client;
    }

    private void DisposeClient(IImapClient client)
    {
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
            _logger.Debug(ex, "Error disposing client");
        }
    }

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

    public string GetProtocolLogContent()
    {
        if (_protocolLogStream == null) return default;

        if (_protocolLogStream.CanSeek)
            _protocolLogStream.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(_protocolLogStream, Encoding.UTF8, true, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    // Legacy compatibility methods
    public Task<bool> EnsureConnectedAsync(IImapClient client) =>
        Task.FromResult(client.IsConnected);

    public Task EnsureAuthenticatedAsync(IImapClient client) =>
        Task.CompletedTask;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _maintenanceCts.Cancel();
                _maintenanceTask?.Wait(TimeSpan.FromSeconds(5));
                _maintenanceCts.Dispose();

                _availableClients.Writer.Complete();

                foreach (var kvp in _clientStates)
                {
                    DisposeClient(kvp.Key);
                }
                _clientStates.Clear();

                lock (_idleClientLock)
                {
                    if (_dedicatedIdleClient != null)
                    {
                        DisposeClient(_dedicatedIdleClient);
                        _dedicatedIdleClient = null;
                    }
                }
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
