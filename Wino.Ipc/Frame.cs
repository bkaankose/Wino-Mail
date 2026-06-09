using System;

namespace Wino.Ipc;

/// <summary>
/// A single protocol frame: <c>[4B little-endian length][1B type][16B correlation Guid][payload]</c>.
/// Length covers the type byte, correlation id and payload.
/// </summary>
public sealed class Frame
{
    public const int MaxPayloadLength = 1024 * 1024; // 1 MiB
    public const int HeaderLength = 1 + 16;

    public Frame(FrameType type, Guid correlationId, ReadOnlyMemory<byte> payload = default)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Frame payload of {payload.Length} bytes exceeds the {MaxPayloadLength} byte limit.");

        Type = type;
        CorrelationId = correlationId;
        Payload = payload;
    }

    public FrameType Type { get; }

    public Guid CorrelationId { get; }

    public ReadOnlyMemory<byte> Payload { get; }
}
