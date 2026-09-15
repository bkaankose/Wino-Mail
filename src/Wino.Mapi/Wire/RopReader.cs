using System.Buffers.Binary;
using System.Text;

namespace Wino.Mapi.Wire;

/// <summary>
/// Reader counterpart of <see cref="RopWriter"/>. Throws <see cref="MapiFormatException"/> rather than
/// returning junk when a buffer is short. In this protocol a wrong read produces a plausible number,
/// not an error, so the only safe response to running out of bytes is to stop.
/// </summary>
public sealed class RopReader(ReadOnlyMemory<byte> buffer)
{
    private readonly ReadOnlyMemory<byte> _buffer = buffer;
    private int _position;

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;

    public byte UInt8() => Take(1).Span[0];

    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2).Span);

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4).Span);

    public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8).Span);

    public ReadOnlyMemory<byte> Bytes(int count) => Take(count);

    public string AsciiZ()
    {
        var span = _buffer.Span[_position..];
        var end = span.IndexOf((byte)0);
        if (end < 0)
        {
            throw new MapiFormatException("Unterminated ASCII string in MAPI buffer.");
        }

        var value = Encoding.ASCII.GetString(span[..end]);
        _position += end + 1;
        return value;
    }

    public string UnicodeZ()
    {
        var span = _buffer.Span[_position..];
        for (var i = 0; i + 1 < span.Length; i += 2)
        {
            if (span[i] == 0 && span[i + 1] == 0)
            {
                var value = Encoding.Unicode.GetString(span[..i]);
                _position += i + 2;
                return value;
            }
        }

        throw new MapiFormatException("Unterminated Unicode string in MAPI buffer.");
    }

    private ReadOnlyMemory<byte> Take(int count)
    {
        if (count < 0 || Remaining < count)
        {
            throw new MapiFormatException($"MAPI buffer underrun: wanted {count} bytes at offset {_position}, {Remaining} remain.");
        }

        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }
}
