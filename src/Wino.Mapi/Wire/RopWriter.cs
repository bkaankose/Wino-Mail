using System.Buffers.Binary;
using System.Text;

namespace Wino.Mapi.Wire;

/// <summary>
/// Little-endian primitives for MAPI wire buffers (MS-OXCDATA section 2.11). Everything in this
/// protocol is little-endian and tightly packed with no alignment padding.
/// </summary>
public sealed class RopWriter
{
    private readonly MemoryStream _stream = new();

    public int Length => (int)_stream.Length;

    public void UInt8(byte value) => _stream.WriteByte(value);

    public void UInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void UInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void UInt64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);

    /// <summary>Null-terminated 8-bit string. Used for the legacyExchangeDN in Connect.</summary>
    public void AsciiZ(string value)
    {
        _stream.Write(Encoding.ASCII.GetBytes(value));
        _stream.WriteByte(0);
    }

    /// <summary>Null-terminated UTF-16LE string.</summary>
    public void UnicodeZ(string value)
    {
        _stream.Write(Encoding.Unicode.GetBytes(value));
        UInt16(0);
    }

    public byte[] ToArray() => _stream.ToArray();
}
