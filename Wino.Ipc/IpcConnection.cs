using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wino.Ipc;

/// <summary>
/// Owns a duplex stream and provides:
/// - a single writer loop multiplexing two outgoing channels: a bounded RPC channel
///   (requests/responses, waits when full) and a bounded event channel that drops the
///   oldest entries so a stalled peer can never wedge synchronization,
/// - a reader loop that surfaces incoming frames via <see cref="FrameReceived"/>.
/// </summary>
public sealed class IpcConnection : IAsyncDisposable
{
    private const int RpcChannelCapacity = 256;
    private const int EventChannelCapacity = 512;

    private readonly Stream _stream;
    private readonly Channel<Frame> _rpcChannel;
    private readonly Channel<Frame> _eventChannel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _writerLoop;
    private Task? _readerLoop;

    public IpcConnection(Stream stream)
    {
        _stream = stream;

        _rpcChannel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(RpcChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _eventChannel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(EventChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    /// <summary>Raised from the reader loop for every incoming frame.</summary>
    public event Action<Frame>? FrameReceived;

    /// <summary>Raised once when the connection terminates for any reason.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Completes when the connection has terminated.</summary>
    public Task Completion => _closedTcs.Task;

    public bool IsClosed => _closedTcs.Task.IsCompleted;

    public void Start()
    {
        _writerLoop = Task.Run(WriterLoopAsync);
        _readerLoop = Task.Run(ReaderLoopAsync);
    }

    /// <summary>Queues an RPC frame; waits when the channel is full (backpressure).</summary>
    public async ValueTask SendAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rpcChannel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new IOException("IPC connection is closed.");
        }
    }

    /// <summary>
    /// Queues an event frame. Never blocks; the channel drops the oldest events when the
    /// peer cannot keep up. Returns false when the connection is closed.
    /// </summary>
    public bool TrySendEvent(Frame frame) => _eventChannel.Writer.TryWrite(frame);

    private async Task WriterLoopAsync()
    {
        Exception? fault = null;

        try
        {
            var rpcReader = _rpcChannel.Reader;
            var eventReader = _eventChannel.Reader;

            while (!_lifetimeCts.IsCancellationRequested)
            {
                // RPC traffic has priority over events.
                if (rpcReader.TryRead(out var frame) || eventReader.TryRead(out frame))
                {
                    await FrameProtocol.WriteFrameAsync(_stream, frame, _lifetimeCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (rpcReader.Completion.IsCompleted && eventReader.Completion.IsCompleted)
                    break;

                var rpcWait = rpcReader.WaitToReadAsync(_lifetimeCts.Token).AsTask();
                var eventWait = eventReader.WaitToReadAsync(_lifetimeCts.Token).AsTask();

                await Task.WhenAny(rpcWait, eventWait).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            fault = exception;
        }
        finally
        {
            CompleteClose(fault);
        }
    }

    private async Task ReaderLoopAsync()
    {
        Exception? fault = null;

        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                var frame = await FrameProtocol.ReadFrameAsync(_stream, _lifetimeCts.Token).ConfigureAwait(false);

                if (frame == null)
                    break;

                FrameReceived?.Invoke(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            fault = exception;
        }
        finally
        {
            CompleteClose(fault);
        }
    }

    private void CompleteClose(Exception? fault)
    {
        var firstClose = fault == null
            ? _closedTcs.TrySetResult()
            : _closedTcs.TrySetException(fault);

        if (!firstClose)
            return;

        // Observe the completion exception so it never surfaces as unobserved.
        _ = _closedTcs.Task.Exception;

        _lifetimeCts.Cancel();
        _rpcChannel.Writer.TryComplete();
        _eventChannel.Writer.TryComplete();

        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Disposing a broken pipe stream can throw; nothing to do.
        }

        Closed?.Invoke(fault);
    }

    public async ValueTask DisposeAsync()
    {
        CompleteClose(null);

        if (_writerLoop != null)
        {
            try { await _writerLoop.ConfigureAwait(false); } catch { }
        }

        if (_readerLoop != null)
        {
            try { await _readerLoop.ConfigureAwait(false); } catch { }
        }

        _lifetimeCts.Dispose();
    }
}
