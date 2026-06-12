using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Ipc.Protocol;

namespace Wino.Ipc;

/// <summary>
/// Options for a server side connection.
/// </summary>
public sealed class RpcServerConnectionOptions
{
    public required int ProtocolVersion { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>Maps a handler exception to an error envelope. Defaults to a generic mapping.</summary>
    public Func<Exception, RpcErrorEnvelope>? ExceptionMapper { get; init; }

    /// <summary>Shared across connections so retried writes are deduplicated after reconnects.</summary>
    public RpcOperationDeduplicator? OperationDeduplicator { get; init; }

    /// <summary>Payloads larger than this are split into StreamChunk/StreamEnd frames.</summary>
    public int ChunkThreshold { get; init; } = 512 * 1024;
}

/// <summary>
/// Server side of one duplex RPC connection: performs the handshake, dispatches requests
/// to the <see cref="IRpcRequestHandler"/> with per-call cancellation, chunks oversized
/// responses and pushes event frames.
/// </summary>
public sealed class RpcServerConnection : IAsyncDisposable
{
    private readonly IpcConnection _connection;
    private readonly IRpcRequestHandler _requestHandler;
    private readonly RpcServerConnectionOptions _options;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlightCalls = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _handshakeCompleted;
    private int _disposeStarted;

    public RpcServerConnection(Stream stream, IRpcRequestHandler requestHandler, RpcServerConnectionOptions options)
    {
        _requestHandler = requestHandler;
        _options = options;
        _connection = new IpcConnection(stream);
        _connection.FrameReceived += OnFrameReceived;
        _connection.Closed += OnConnectionClosed;
    }

    /// <summary>Raised once when the connection terminates.</summary>
    public event Action<RpcServerConnection, Exception?>? Closed;

    /// <summary>Raised after a successful handshake.</summary>
    public event Action<RpcServerConnection, HandshakeRequest>? HandshakeCompleted;

    public Task Completion => _connection.Completion;

    public void Start() => _connection.Start();

    /// <summary>
    /// Pushes an event envelope to the client. Never blocks; drops oldest under pressure.
    /// </summary>
    public bool TryPublishEvent(string eventTypeName, ReadOnlyMemory<byte> payloadUtf8Json)
    {
        if (!_handshakeCompleted || _connection.IsClosed)
            return false;

        var envelope = RpcEnvelope.WriteEvent(eventTypeName, payloadUtf8Json);
        return _connection.TrySendEvent(new Frame(FrameType.Event, Guid.NewGuid(), envelope));
    }

    private void OnFrameReceived(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameType.Handshake:
                _ = HandleHandshakeAsync(frame);
                break;

            case FrameType.Request:
                _ = HandleRequestAsync(frame);
                break;

            case FrameType.Cancel:
                if (_inFlightCalls.TryGetValue(frame.CorrelationId, out var callCts))
                {
                    try { callCts.Cancel(); } catch (ObjectDisposedException) { }
                }
                break;

            case FrameType.Ping:
                _ = SendSafeAsync(new Frame(FrameType.Pong, frame.CorrelationId));
                break;
        }
    }

    private async Task HandleHandshakeAsync(Frame frame)
    {
        HandshakeRequest? request = null;
        bool accepted = false;
        string? message = null;

        try
        {
            request = JsonSerializer.Deserialize(frame.Payload.Span, IpcProtocolJsonContext.Default.HandshakeRequest);

            if (request == null)
            {
                message = "Malformed handshake.";
            }
            else if (request.ProtocolVersion != _options.ProtocolVersion)
            {
                message = $"Protocol version mismatch: client {request.ProtocolVersion}, server {_options.ProtocolVersion}.";
            }
            else
            {
                accepted = true;
            }
        }
        catch (Exception exception)
        {
            message = $"Handshake failed: {exception.Message}";
        }

        var response = new HandshakeResponse(_options.ProtocolVersion, _options.AppVersion, accepted, message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, IpcProtocolJsonContext.Default.HandshakeResponse);

        await SendSafeAsync(new Frame(FrameType.HandshakeAck, frame.CorrelationId, payload)).ConfigureAwait(false);

        if (accepted && request != null)
        {
            _handshakeCompleted = true;
            HandshakeCompleted?.Invoke(this, request);
        }
    }

    private async Task HandleRequestAsync(Frame frame)
    {
        var correlationId = frame.CorrelationId;
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

        if (!_inFlightCalls.TryAdd(correlationId, callCts))
            return;

        try
        {
            RpcRequestEnvelope envelope;

            try
            {
                envelope = RpcEnvelope.ParseRequest(frame.Payload);
            }
            catch (Exception parseException)
            {
                await SendErrorAsync(correlationId, new RpcErrorEnvelope(RpcErrorTypes.Protocol, $"Malformed request envelope: {parseException.Message}")).ConfigureAwait(false);
                return;
            }

            // Retried write after a reconnect? Replay the recorded response.
            if (envelope.OperationId.HasValue &&
                _options.OperationDeduplicator != null &&
                _options.OperationDeduplicator.TryGetCompletedResponse(envelope.OperationId.Value, out var cachedResponse))
            {
                await SendSuccessAsync(correlationId, cachedResponse).ConfigureAwait(false);
                return;
            }

            byte[]? responsePayload;

            try
            {
                responsePayload = await _requestHandler.HandleRequestAsync(envelope.MethodName, envelope.Payload, callCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callCts.IsCancellationRequested)
            {
                await SendErrorAsync(correlationId, new RpcErrorEnvelope(RpcErrorTypes.Canceled, "The call was canceled.")).ConfigureAwait(false);
                return;
            }
            catch (Exception handlerException)
            {
                var error = _options.ExceptionMapper?.Invoke(handlerException)
                    ?? new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            handlerException.Message,
                                            handlerException.GetType().FullName,
                                            handlerException.StackTrace);

                await SendErrorAsync(correlationId, error).ConfigureAwait(false);
                return;
            }

            if (envelope.OperationId.HasValue)
            {
                _options.OperationDeduplicator?.RecordCompletedOperation(envelope.OperationId.Value, responsePayload);
            }

            await SendSuccessAsync(correlationId, responsePayload).ConfigureAwait(false);
        }
        finally
        {
            _inFlightCalls.TryRemove(correlationId, out _);
        }
    }

    private async Task SendSuccessAsync(Guid correlationId, byte[]? payload)
    {
        payload ??= [];

        if (payload.Length <= _options.ChunkThreshold)
        {
            await SendSafeAsync(new Frame(FrameType.ResponseSuccess, correlationId, payload)).ConfigureAwait(false);
            return;
        }

        // Chunk oversized payloads: N StreamChunk frames followed by a StreamEnd frame.
        var offset = 0;

        while (payload.Length - offset > _options.ChunkThreshold)
        {
            await SendSafeAsync(new Frame(FrameType.StreamChunk, correlationId, payload.AsMemory(offset, _options.ChunkThreshold))).ConfigureAwait(false);
            offset += _options.ChunkThreshold;
        }

        await SendSafeAsync(new Frame(FrameType.StreamEnd, correlationId, payload.AsMemory(offset))).ConfigureAwait(false);
    }

    private Task SendErrorAsync(Guid correlationId, RpcErrorEnvelope error)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(error, IpcProtocolJsonContext.Default.RpcErrorEnvelope);
        return SendSafeAsync(new Frame(FrameType.ResponseError, correlationId, payload));
    }

    private async Task SendSafeAsync(Frame frame)
    {
        try
        {
            await _connection.SendAsync(frame, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Connection is gone; nothing meaningful to do on the server side.
        }
    }

    private void OnConnectionClosed(Exception? fault)
    {
        TryCancelLifetime();
        Closed?.Invoke(this, fault);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
            return;

        TryCancelLifetime();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
    }

    private void TryCancelLifetime()
    {
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
