using System.Threading.Channels;

namespace Wino.Ipc.Tests;

/// <summary>
/// In-memory duplex stream pair for protocol tests: bytes written to one side become
/// readable on the other. Disposing either side ends the peer's reads (like a broken pipe).
/// </summary>
public sealed class InMemoryDuplexStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private byte[]? _currentSegment;
    private int _currentOffset;

    private InMemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (InMemoryDuplexStream Client, InMemoryDuplexStream Server) CreatePair()
    {
        var clientToServer = Channel.CreateUnbounded<byte[]>();
        var serverToClient = Channel.CreateUnbounded<byte[]>();

        var client = new InMemoryDuplexStream(serverToClient, clientToServer);
        var server = new InMemoryDuplexStream(clientToServer, serverToClient);

        return (client, server);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_currentSegment == null || _currentOffset >= _currentSegment.Length)
        {
            try
            {
                _currentSegment = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _currentOffset = 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        var available = _currentSegment.Length - _currentOffset;
        var toCopy = Math.Min(available, buffer.Length);

        _currentSegment.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
        _currentOffset += toCopy;

        return toCopy;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await _outgoing.Writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new IOException("Stream is closed.");
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Closing our outbound direction ends the peer's reads; closing inbound ends ours.
            _outgoing.Writer.TryComplete();
            _incoming.Writer.TryComplete();
        }

        base.Dispose(disposing);
    }
}
