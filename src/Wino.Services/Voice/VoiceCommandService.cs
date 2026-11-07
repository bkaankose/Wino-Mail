#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Services.Voice;

public sealed class VoiceCommandService(IWinoAccountApiClient apiClient) : IVoiceCommandService
{
    public async Task<VoiceCommandStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        VoiceConnectResult connection;
        try
        {
            connection = await apiClient.ConnectVoiceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the voice socket.");
            return new VoiceCommandStartResult(null, WinoAccountClientErrorCodes.ServiceUnavailable);
        }

        if (connection.Socket is not { } socket)
            return new VoiceCommandStartResult(null, connection.ErrorCode ?? WinoAccountClientErrorCodes.ServiceUnavailable);

        var session = new VoiceCommandSession(socket);
        session.Start(CreateContext());
        return new VoiceCommandStartResult(session, null);
    }

    private static VoiceInterpretContext CreateContext()
    {
        var languages = new List<string>();
        var uiLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (uiLanguage.Length == 2)
            languages.Add(uiLanguage);
        if (!languages.Contains("en"))
            languages.Add("en");

        return new VoiceInterpretContext(TimeZoneInfo.Local.Id, DateTime.Now, languages);
    }
}

/// <summary>
/// The client side of one interpret socket. A single send loop keeps WebSocket sends ordered (start, audio, commit)
/// and holds audio until the API reports it is ready; a single receive loop parses the server frames.
/// </summary>
internal sealed class VoiceCommandSession(WebSocket socket) : IVoiceCommandSession
{
    private readonly Channel<VoiceServerFrame> _frames = Channel.CreateUnbounded<VoiceServerFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private int _isCommitted;
    private int _isFinished;
    private int _isDisposed;

    public ChannelReader<VoiceServerFrame> Frames => _frames.Reader;

    public void Start(VoiceInterpretContext context)
    {
        Enqueue(new VoiceClientFrame(VoiceFrameTypes.Start, context));
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
            Enqueue(new VoiceClientFrame(VoiceFrameTypes.Commit));
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
            Log.Debug(ex, "Could not abort the voice socket.");
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

    private void Enqueue(VoiceClientFrame frame)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(frame, VoiceJsonContext.Default.VoiceClientFrame);
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
            Log.Warning(ex, "The voice socket stopped sending.");
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
                Log.Warning(ex, "The voice socket stopped receiving.");
            Finish(VoiceClientErrorCodes.ConnectionLost);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleFrame(ReadOnlySpan<byte> json)
    {
        VoiceServerFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize(json, VoiceJsonContext.Default.VoiceServerFrame);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "The voice socket sent a frame the app could not read.");
            Finish(VoiceClientErrorCodes.InvalidFrame);
            return;
        }

        if (frame is null || Volatile.Read(ref _isFinished) == 1)
            return;

        switch (frame.Type)
        {
            case VoiceFrameTypes.Ready:
                _ready.TrySetResult();
                break;
            case VoiceFrameTypes.Plan:
            case VoiceFrameTypes.Error:
                if (Interlocked.Exchange(ref _isFinished, 1) == 1)
                    return;
                _frames.Writer.TryWrite(frame);
                _frames.Writer.TryComplete();
                return;
        }

        _frames.Writer.TryWrite(frame);
    }

    /// <summary>Ends the frames with a client error unless the API already sent its plan or error.</summary>
    private void Finish(string errorCode)
    {
        if (Interlocked.Exchange(ref _isFinished, 1) == 1)
            return;

        _ready.TrySetCanceled();
        if (Volatile.Read(ref _isDisposed) == 0)
            _frames.Writer.TryWrite(VoiceServerFrame.Error(errorCode, string.Empty));
        _frames.Writer.TryComplete();
    }

    private readonly record struct Outgoing(ReadOnlyMemory<byte> Data, WebSocketMessageType Type);
}
