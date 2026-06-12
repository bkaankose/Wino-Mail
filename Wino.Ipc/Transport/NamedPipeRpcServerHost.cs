using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Ipc.Protocol;

namespace Wino.Ipc.Transport;

/// <summary>
/// Accept loop for the background service: keeps a listening pipe instance available
/// (up to <see cref="NamedPipeTransport.MaxServerInstances"/> concurrent clients) and
/// spins up an <see cref="RpcServerConnection"/> for every client that connects.
/// </summary>
public sealed class NamedPipeRpcServerHost : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IRpcRequestHandler _requestHandler;
    private readonly RpcServerConnectionOptions _connectionOptions;
    private readonly ConcurrentDictionary<RpcServerConnection, byte> _activeConnections = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _acceptLoop;

    public NamedPipeRpcServerHost(string pipeName, IRpcRequestHandler requestHandler, RpcServerConnectionOptions connectionOptions)
    {
        _pipeName = pipeName;
        _requestHandler = requestHandler;
        _connectionOptions = connectionOptions;
    }

    /// <summary>Number of currently connected clients.</summary>
    public int ConnectionCount => _activeConnections.Count;

    /// <summary>Raised when a client completes the handshake.</summary>
    public event Action<RpcServerConnection, HandshakeRequest>? ClientConnected;

    /// <summary>Raised when a client connection terminates. Second argument is the remaining connection count.</summary>
    public event Action<RpcServerConnection, int>? ClientDisconnected;

    public Task Start()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return _listenerReady.Task;
    }

    /// <summary>
    /// Pushes an event to every connected client.
    /// </summary>
    public void PublishEvent(string eventTypeName, ReadOnlyMemory<byte> payloadUtf8Json)
    {
        foreach (var connection in _activeConnections.Keys)
        {
            connection.TryPublishEvent(eventTypeName, payloadUtf8Json);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetimeCts.IsCancellationRequested)
        {
            try
            {
                if (_activeConnections.Count >= NamedPipeTransport.MaxServerInstances)
                {
                    // All slots busy; wait for one to free up before listening again.
                    await Task.WhenAny(_activeConnections.Keys.Select(c => c.Completion).ToArray()).ConfigureAwait(false);
                    continue;
                }

                var serverStream = NamedPipeTransport.CreateServerStream(_pipeName);
                _listenerReady.TrySetResult();

                try
                {
                    await serverStream.WaitForConnectionAsync(_lifetimeCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    await serverStream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var connection = new RpcServerConnection(serverStream, _requestHandler, _connectionOptions);

                connection.HandshakeCompleted += (conn, handshake) => ClientConnected?.Invoke(conn, handshake);
                connection.Closed += OnConnectionClosed;

                _activeConnections.TryAdd(connection, 0);
                connection.Start();
            }
            catch (OperationCanceledException)
            {
                _listenerReady.TrySetCanceled(_lifetimeCts.Token);
                break;
            }
            catch
            {
                // Listening failed (e.g. transient pipe error); back off briefly and retry.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void OnConnectionClosed(RpcServerConnection connection, Exception? fault)
    {
        if (_activeConnections.TryRemove(connection, out _))
        {
            _ = connection.DisposeAsync().AsTask();
            ClientDisconnected?.Invoke(connection, _activeConnections.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();

        foreach (var connection in _activeConnections.Keys)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _activeConnections.Clear();

        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }

        _lifetimeCts.Dispose();
    }
}
