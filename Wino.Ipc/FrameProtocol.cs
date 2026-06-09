using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Ipc;

/// <summary>
/// Reads and writes <see cref="Frame"/>s over any duplex <see cref="Stream"/>.
/// Purely stream based so the protocol can be unit tested in memory;
/// named pipes only appear in the transport classes.
/// </summary>
public static class FrameProtocol
{
    public static async ValueTask WriteFrameAsync(Stream stream, Frame frame, CancellationToken cancellationToken = default)
    {
        var totalLength = Frame.HeaderLength + frame.Payload.Length;
        var buffer = new byte[4 + totalLength];

        BinaryPrimitives.WriteInt32LittleEndian(buffer, totalLength);
        buffer[4] = (byte)frame.Type;
        frame.CorrelationId.TryWriteBytes(buffer.AsSpan(5, 16));
        frame.Payload.Span.CopyTo(buffer.AsSpan(4 + Frame.HeaderLength));

        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the next frame from the stream. Returns null when the stream is closed cleanly
    /// at a frame boundary.
    /// </summary>
    /// <exception cref="InvalidDataException">Frame is malformed or exceeds size limits.</exception>
    /// <exception cref="EndOfStreamException">Stream ended in the middle of a frame.</exception>
    public static async ValueTask<Frame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[4];

        var firstRead = await stream.ReadAsync(lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (firstRead == 0) return null;

        await FillBufferAsync(stream, lengthBuffer.AsMemory(firstRead, 4 - firstRead), cancellationToken).ConfigureAwait(false);

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

        if (totalLength < Frame.HeaderLength || totalLength > Frame.HeaderLength + Frame.MaxPayloadLength)
            throw new InvalidDataException($"Invalid frame length {totalLength}.");

        var frameBuffer = new byte[totalLength];
        await FillBufferAsync(stream, frameBuffer, cancellationToken).ConfigureAwait(false);

        var type = (FrameType)frameBuffer[0];
        var correlationId = new Guid(frameBuffer.AsSpan(1, 16));
        var payload = frameBuffer.AsMemory(Frame.HeaderLength);

        return new Frame(type, correlationId, payload);
    }

    private static async ValueTask FillBufferAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
                throw new EndOfStreamException("Stream closed in the middle of a frame.");

            buffer = buffer[read..];
        }
    }
}
