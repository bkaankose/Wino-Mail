using System.Buffers.Binary;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Ics;

/// <summary>
/// Serialized IDSET decoding (MS-OXCFXICS 2.2.2.4). An IDSET is a list of (REPLGUID, GLOBSET); a
/// GLOBSET is a compact command stream that yields 6-byte global counters:
///
///   0x01..0x06  push N bytes onto the common-prefix stack
///   0x50        pop
///   0x42        bitmask: one start byte, then a byte whose set bits name the following 8 values
///   0x52        range: low and high 6-byte tails (after the prefix)
///   0x00        end
///
/// Each yielded value is DatabaseGuid + counter, which is exactly a long-term id
/// (<see cref="RopIds.LongTermIdLength"/>), so results feed RopIdFromLongTermId directly.
/// </summary>
public static class IdSet
{
    /// <summary>The REPLGUID form: each entry is a 16-byte database GUID, then a GLOBSET. Used by the ICS state properties.</summary>
    public static List<byte[]> DecodeLongTermIds(ReadOnlySpan<byte> serialized)
    {
        var result = new List<byte[]>();
        var reader = new RopReader(serialized.ToArray());

        while (reader.Remaining >= 16)
        {
            var replGuid = reader.Bytes(16).ToArray();
            DecodeGlobSet(reader, replGuid, result);
        }

        return result;
    }

    /// <summary>
    /// The REPLID form: each entry is a 2-byte replica id (this logon's), then a GLOBSET. This is what
    /// the download stream uses for MetaTagIdsetDeleted / Read / Unread (found live: a read-state
    /// section of 10 bytes cannot hold a GUID). A replica id plus a 6-byte counter IS a message id in
    /// wire byte order, so the results are ids directly, with no long-term-id conversion.
    /// </summary>
    public static List<ulong> DecodeIds(ReadOnlySpan<byte> serialized)
    {
        var longTermStyle = new List<byte[]>();
        var reader = new RopReader(serialized.ToArray());

        while (reader.Remaining >= 3)
        {
            // Reuse the GLOBSET walker with a 16-byte "guid" whose first two bytes are the replid;
            // only the counter bytes and those two matter afterwards.
            var replId = reader.Bytes(2).ToArray();
            var pseudoGuid = new byte[16];
            replId.CopyTo(pseudoGuid, 0);
            DecodeGlobSet(reader, pseudoGuid, longTermStyle);
        }

        var ids = new List<ulong>(longTermStyle.Count);
        Span<byte> wire = stackalloc byte[8];
        foreach (var entry in longTermStyle)
        {
            // Wire order of a message id: ReplicaId (2, little-endian) then GlobalCounter (6). The
            // ulong form used everywhere else is the little-endian read of those 8 bytes.
            wire[0] = entry[0];
            wire[1] = entry[1];
            entry.AsSpan(16, 6).CopyTo(wire[2..]);
            ids.Add(BinaryPrimitives.ReadUInt64LittleEndian(wire));
        }

        return ids;
    }

    private static void DecodeGlobSet(RopReader reader, byte[] replGuid, List<byte[]> result)
    {
        // The common-bytes stack: each push is one segment, so a pop can remove exactly what the
        // matching push added (MS-OXCFXICS 2.2.2.6.1: pop "removes the bytes pushed by the most
        // recent push command").
        var stack = new List<byte[]>();
        var prefixLength = 0;

        void Emit(ReadOnlySpan<byte> tail)
        {
            var id = new byte[RopIds.LongTermIdLength];
            replGuid.CopyTo(id, 0);
            var counter = id.AsSpan(16, 6);
            var offset = 0;
            foreach (var segment in stack)
            {
                segment.CopyTo(counter[offset..]);
                offset += segment.Length;
            }

            tail.CopyTo(counter[offset..]);
            result.Add(id);
        }

        while (true)
        {
            var command = reader.UInt8();
            switch (command)
            {
                case 0x00:
                    return;

                case >= 0x01 and <= 0x06:
                {
                    var pushed = reader.Bytes(command).ToArray();
                    if (prefixLength + pushed.Length == 6)
                    {
                        // A full 6-byte value: the push is the value itself and is not kept on the stack.
                        Emit(pushed);
                    }
                    else if (prefixLength + pushed.Length > 6)
                    {
                        throw new MapiFormatException("GLOBSET push overflows the 6-byte counter; the stream is misread.");
                    }
                    else
                    {
                        stack.Add(pushed);
                        prefixLength += pushed.Length;
                    }

                    break;
                }

                case 0x50:
                {
                    if (stack.Count > 0)
                    {
                        prefixLength -= stack[^1].Length;
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
                }

                case 0x42:
                {
                    var start = reader.UInt8();
                    var mask = reader.UInt8();
                    if (6 - prefixLength != 1)
                        throw new MapiFormatException($"GLOBSET bitmask with a {prefixLength}-byte prefix; expected 5.");

                    Emit([start]);
                    for (var bit = 0; bit < 8; bit++)
                    {
                        if ((mask & (1 << bit)) != 0)
                            Emit([(byte)(start + bit + 1)]);
                    }

                    break;
                }

                case 0x52:
                {
                    var width = 6 - prefixLength;
                    var low = reader.Bytes(width).ToArray();
                    var high = reader.Bytes(width).ToArray();
                    var lowValue = ToBigEndianValue(low);
                    var highValue = ToBigEndianValue(high);
                    if (highValue - lowValue > 1_000_000)
                        throw new MapiFormatException("GLOBSET range is implausibly large; the stream is misread.");

                    for (var value = lowValue; value <= highValue; value++)
                        Emit(FromBigEndianValue(value, width));

                    break;
                }

                default:
                    throw new MapiFormatException($"Unknown GLOBSET command 0x{command:X2}.");
            }
        }
    }

    // Global counters are big-endian on the wire (MS-OXCFXICS 2.2.2.6): the most significant byte first.
    private static ulong ToBigEndianValue(byte[] bytes)
    {
        ulong value = 0;
        foreach (var b in bytes)
            value = (value << 8) | b;
        return value;
    }

    private static byte[] FromBigEndianValue(ulong value, int width)
    {
        var bytes = new byte[width];
        for (var i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }
}
