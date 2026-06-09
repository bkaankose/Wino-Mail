using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wino.Ipc.Protocol;

namespace Wino.Ipc;

/// <summary>
/// Client side of the RPC protocol over a single duplex connection.
/// Supports unlimited concurrent in-flight calls (correlation id → completion source),
/// cancellation forwarding, chunked response reassembly and server pushed events.
/// </summary>
public sealed class RpcClient : IRpcClient, IAsyncDisposable
{
    private readonly IpcConnection _connection;
    private readonly ConcurrentDictionary<Guid, PendingCall> _pendingCalls = new();
    private readonly Func<RpcErrorEnvelope, Exception?>? _domainExceptionMapper;

    private TaskCompletionSource<HandshakeResponse>? _handshakeTcs;
    private TaskCompletionSource? _pongTcs;

    public RpcClient(Stream stream, Func<RpcErrorEnvelope, Exception?>? domainExceptionMapper = null)
    {
        _domainExceptionMapper = domainExceptionMapper;
        _connection = new IpcConnection(stream);
        _connection.FrameReceived += OnFrameReceived;
        _connection.Closed += OnConnectionClosed;
        _connection.Start();
    }

    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised once when the underlying connection terminates.</summary>
    public event Action<Exception?>? ConnectionClosed;

    public bool IsConnected => !_connection.IsClosed;

    /// <summary>
    /// Performs the protocol handshake. Must be called once before any invocation.
    /// </summary>
    public async Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken = default)
    {
        _handshakeTcs = new TaskCompletionSource<HandshakeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, IpcProtocolJsonContext.Default.HandshakeRequest);
        await _connection.SendAsync(new Frame(FrameType.Handshake, Guid.NewGuid(), payload), cancellationToken).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => _handshakeTcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false);
        return await _handshakeTcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a ping and waits for the matching pong. Used by the connection health loop.
    /// </summary>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        _pongTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _connection.SendAsync(new Frame(FrameType.Ping, Guid.NewGuid()), cancellationToken).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => _pongTcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false);
        await _pongTcs.Task.ConfigureAwait(false);
    }

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(string methodName,
                                                                  TRequest request,
                                                                  JsonTypeInfo<TRequest> requestTypeInfo,
                                                                  JsonTypeInfo<TResponse> responseTypeInfo,
                                                                  Guid? operationId = null,
                                                                  CancellationToken cancellationToken = default)
    {
        var responseBytes = await InvokeCoreAsync(methodName, request, requestTypeInfo, operationId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(responseBytes, responseTypeInfo)!;
    }

    public async Task InvokeAsync<TRequest>(string methodName,
                                            TRequest request,
                                            JsonTypeInfo<TRequest> requestTypeInfo,
                                            Guid? operationId = null,
                                            CancellationToken cancellationToken = default)
    {
        await InvokeCoreAsync(methodName, request, requestTypeInfo, operationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> InvokeCoreAsync<TRequest>(string methodName,
                                                         TRequest request,
                                                         JsonTypeInfo<TRequest> requestTypeInfo,
                                                         Guid? operationId,
                                                         CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var correlationId = Guid.NewGuid();
        var pendingCall = new PendingCall();

        if (!_pendingCalls.TryAdd(correlationId, pendingCall))
            throw new InvalidOperationException("Correlation id collision.");

        try
        {
            var payload = RpcEnvelope.WriteRequest(methodName, operationId, request, requestTypeInfo);
            await _connection.SendAsync(new Frame(FrameType.Request, correlationId, payload), cancellationToken).ConfigureAwait(false);

            CancellationTokenRegistration registration = default;

            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() =>
                {
                    // Fire-and-forget the cancel frame; the server replies with a canceled error.
                    _ = SendCancelAsync(correlationId);
                });
            }

            await using (registration.ConfigureAwait(false))
            {
                return await pendingCall.Completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pendingCalls.TryRemove(correlationId, out _);
        }
    }

    private async Task SendCancelAsync(Guid correlationId)
    {
        try
        {
            await _connection.SendAsync(new Frame(FrameType.Cancel, correlationId)).ConfigureAwait(false);
        }
        catch
        {
            // Connection is gone; the pending call will be failed by OnConnectionClosed.
        }
    }

    private void OnFrameReceived(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameType.HandshakeAck:
                var handshakeResponse = JsonSerializer.Deserialize(frame.Payload.Span, IpcProtocolJsonContext.Default.HandshakeResponse);
                _handshakeTcs?.TrySetResult(handshakeResponse!);
                break;

            case FrameType.ResponseSuccess:
                if (_pendingCalls.TryGetValue(frame.CorrelationId, out var successCall))
                    successCall.Completion.TrySetResult(frame.Payload.ToArray());
                break;

            case FrameType.StreamChunk:
                if (_pendingCalls.TryGetValue(frame.CorrelationId, out var chunkCall))
                    chunkCall.AppendChunk(frame.Payload.Span);
                break;

            case FrameType.StreamEnd:
                if (_pendingCalls.TryGetValue(frame.CorrelationId, out var endCall))
                {
                    endCall.AppendChunk(frame.Payload.Span);
                    endCall.Completion.TrySetResult(endCall.GetAssembledPayload());
                }
                break;

            case FrameType.ResponseError:
                if (_pendingCalls.TryGetValue(frame.CorrelationId, out var failedCall))
                {
                    var error = JsonSerializer.Deserialize(frame.Payload.Span, IpcProtocolJsonContext.Default.RpcErrorEnvelope)!;
                    failedCall.Completion.TrySetException(MapError(error));
                }
                break;

            case FrameType.Event:
                var eventEnvelope = RpcEnvelope.ParseEvent(frame.Payload);
                EventReceived?.Invoke(eventEnvelope.EventTypeName, eventEnvelope.Payload);
                break;

            case FrameType.Pong:
                _pongTcs?.TrySetResult();
                break;

            case FrameType.Ping:
                _ = SendPongAsync(frame.CorrelationId);
                break;
        }
    }

    private async Task SendPongAsync(Guid correlationId)
    {
        try
        {
            await _connection.SendAsync(new Frame(FrameType.Pong, correlationId)).ConfigureAwait(false);
        }
        catch
        {
            // Connection is gone.
        }
    }

    private Exception MapError(RpcErrorEnvelope error)
    {
        if (error.ErrorType == RpcErrorTypes.Canceled)
            return new OperationCanceledException(error.Message ?? "The remote call was canceled.");

        var mapped = _domainExceptionMapper?.Invoke(error);
        return mapped ?? new WinoRpcRemoteException(error);
    }

    private void OnConnectionClosed(Exception? fault)
    {
        var failure = new WinoRpcConnectionLostException("The IPC connection was lost.", fault);

        foreach (var pendingCall in _pendingCalls.Values)
        {
            pendingCall.Completion.TrySetException(failure);
        }

        _handshakeTcs?.TrySetException(failure);
        _pongTcs?.TrySetException(failure);

        ConnectionClosed?.Invoke(fault);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class PendingCall
    {
        private MemoryStream? _chunkBuffer;

        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AppendChunk(ReadOnlySpan<byte> chunk)
        {
            _chunkBuffer ??= new MemoryStream();
            _chunkBuffer.Write(chunk);
        }

        public byte[] GetAssembledPayload() => _chunkBuffer?.ToArray() ?? [];
    }
}
