#nullable enable
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Services.Assistant;

/// <summary>The interpret socket client: one socket per request, the start frame with the read request, then text or audio, then the reading.</summary>
public sealed class AssistantCommandService(IWinoAccountApiClient apiClient) : IAssistantCommandService
{
    public async Task<AssistantCommandStartResult> StartAsync(AssistantReadRequest request, CancellationToken cancellationToken = default)
    {
        VoiceConnectResult connection;
        try
        {
            connection = await apiClient.ConnectAssistantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the assistant socket.");
            return new AssistantCommandStartResult(null, WinoAccountClientErrorCodes.ServiceUnavailable);
        }

        if (connection.Socket is not { } socket)
            return new AssistantCommandStartResult(null, connection.ErrorCode ?? WinoAccountClientErrorCodes.ServiceUnavailable);

        var session = new AssistantCommandSession(socket);
        session.Start(request with { Text = null, Hints = null });
        return new AssistantCommandStartResult(session, null);
    }
}

/// <summary>
/// The client side of one interpret socket. A single send loop keeps WebSocket sends ordered (start, audio, commit)
/// and holds audio until the API reports it is ready; a single receive loop parses the server frames.
/// </summary>
internal sealed class AssistantCommandSession(WebSocket socket) : IAssistantCommandSession
{
    private readonly Channel<AssistantServerFrame> _frames = Channel.CreateUnbounded<AssistantServerFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private int _isCommitted;
    private int _isFinished;
    private int _isDisposed;

    public ChannelReader<AssistantServerFrame> Frames => _frames.Reader;

    public void Start(AssistantReadRequest request)
    {
        Enqueue(new AssistantClientFrame(AssistantFrameTypes.Start, request));
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public void SendAudio(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.IsEmpty || Volatile.Read(ref _isCommitted) == 1)
            return;

        _outgoing.Writer.TryWrite(new Outgoing(pcm, WebSocketMessageType.Binary));
    }

    public void Commit()
    {
        if (Interlocked.Exchange(ref _isCommitted, 1) == 0)
            Enqueue(new AssistantClientFrame(AssistantFrameTypes.Commit));
    }

    public void SendText(string text)
    {
        if (Interlocked.Exchange(ref _isCommitted, 1) == 0)
            Enqueue(new AssistantClientFrame(AssistantFrameTypes.Text, Text: text));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        _cancellation.Cancel();
        _ready.TrySetCanceled();
        _outgoing.Writer.TryComplete();

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                socket.Abort();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not abort the assistant socket.");
        }

        try
        {
            await Task.WhenAll(_sendLoop, _receiveLoop).ConfigureAwait(false);
        }
        catch
        {
        }

        socket.Dispose();
        _cancellation.Dispose();
        _frames.Writer.TryComplete();
    }

    private void Enqueue(AssistantClientFrame frame)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(frame, AssistantJsonContext.Default.AssistantClientFrame);
        _outgoing.Writer.TryWrite(new Outgoing(json, WebSocketMessageType.Text));
    }

    private async Task SendLoopAsync()
    {
        var token = _cancellation.Token;
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (message.Type == WebSocketMessageType.Binary && !_ready.Task.IsCompleted)
                    await _ready.Task.WaitAsync(token).ConfigureAwait(false);

                if (socket.State != WebSocketState.Open)
                    return;

                await socket.SendAsync(message.Data, message.Type, endOfMessage: true, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The assistant socket stopped sending.");
            Finish(VoiceClientErrorCodes.ConnectionLost);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var token = _cancellation.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    HandleFrame(message.WrittenSpan);

                message.ResetWrittenCount();
            }

            Finish(VoiceClientErrorCodes.ConnectionLost);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isFinished) == 0)
                Log.Warning(ex, "The assistant socket stopped receiving.");
            Finish(VoiceClientErrorCodes.ConnectionLost);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleFrame(ReadOnlySpan<byte> json)
    {
        AssistantServerFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize(json, AssistantJsonContext.Default.AssistantServerFrame);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "The assistant socket sent a frame the app could not read.");
            Finish(VoiceClientErrorCodes.InvalidFrame);
            return;
        }

        if (frame is null || Volatile.Read(ref _isFinished) == 1)
            return;

        switch (frame.Type)
        {
            case AssistantFrameTypes.Ready:
                _ready.TrySetResult();
                break;
            case AssistantFrameTypes.Reading:
            case AssistantFrameTypes.Error:
                if (Interlocked.Exchange(ref _isFinished, 1) == 1)
                    return;
                _frames.Writer.TryWrite(frame);
                _frames.Writer.TryComplete();
                return;
        }

        _frames.Writer.TryWrite(frame);
    }

    /// <summary>Ends the frames with a client error unless the API already sent its reading or error.</summary>
    private void Finish(string errorCode)
    {
        if (Interlocked.Exchange(ref _isFinished, 1) == 1)
            return;

        _ready.TrySetCanceled();
        if (Volatile.Read(ref _isDisposed) == 0)
            _frames.Writer.TryWrite(AssistantServerFrame.Error(errorCode, string.Empty));
        _frames.Writer.TryComplete();
    }

    private readonly record struct Outgoing(ReadOnlyMemory<byte> Data, WebSocketMessageType Type);
}
