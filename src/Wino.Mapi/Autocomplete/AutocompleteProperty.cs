using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Wino.Mapi.Rops;

namespace Wino.Mapi.Autocomplete;

/// <summary>
/// One property on one autocomplete row: a MAPI tag, four reserved bytes, an eight-byte union, and
/// for some types a run of value data after it.
///
/// The value is kept as the raw bytes it arrived as, length prefix and all, and written back the
/// same way. That is deliberate. Only a handful of these properties mean anything to us - the name,
/// the address, the weight - and a row carries others that belong to Outlook. Decoding everything
/// in order to re-encode it would risk changing bytes nobody asked us to change, in somebody's
/// mailbox, for no gain; copying them through cannot.
/// </summary>
public sealed class AutocompleteProperty
{
    public AutocompleteProperty(uint tag, uint reserved, ulong union, byte[] valueData)
    {
        Tag = tag;
        Reserved = reserved;
        Union = union;
        ValueData = valueData;
    }

    public uint Tag { get; }

    public uint Reserved { get; }

    /// <summary>The eight-byte union. Holds the value itself for the fixed-size types.</summary>
    public ulong Union { get; private set; }

    /// <summary>The bytes after the union, exactly as stored, including any length prefix.</summary>
    public byte[] ValueData { get; }

    /// <summary>The low sixteen bits of the tag: what kind of value this is.</summary>
    public ushort Type => TypeOf(Tag);

    /// <summary>A tag's type is its low word (MS-OXCDATA 2.9). Named once, used by both readers.</summary>
    private static ushort TypeOf(uint tag) => (ushort)(tag & 0xFFFF);

    internal static AutocompleteProperty Read(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var tag = AutocompleteStream.ReadUInt32(bytes, ref offset);
        var reserved = AutocompleteStream.ReadUInt32(bytes, ref offset);
        var union = BinaryPrimitives.ReadUInt64LittleEndian(AutocompleteStream.Take(bytes, ref offset, 8));

        var start = offset;
        SkipValue(bytes, ref offset, TypeOf(tag));

        return new AutocompleteProperty(tag, reserved, union, bytes[start..offset].ToArray());
    }

    internal void Write(List<byte> output)
    {
        Span<byte> scratch = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(scratch, Tag);
        output.AddRange(scratch[..4]);

        BinaryPrimitives.WriteUInt32LittleEndian(scratch, Reserved);
        output.AddRange(scratch[..4]);

        BinaryPrimitives.WriteUInt64LittleEndian(scratch, Union);
        output.AddRange(scratch);

        output.AddRange(ValueData);
    }

    /// <summary>Walks past a value without decoding it, which is all the parser needs to stay in step.</summary>
    private static void SkipValue(ReadOnlySpan<byte> bytes, ref int offset, ushort type)
    {
        switch (type)
        {
            // Held in the union; nothing follows.
            case PropertyTypes.Short:
            case PropertyTypes.Long:
            case PropertyTypes.Float:
            case PropertyTypes.Double:
            case PropertyTypes.ErrorCode:
            case PropertyTypes.Boolean:
            case PropertyTypes.SysTime:
            case PropertyTypes.LongLong:
            case PropertyTypes.Currency:
            case PropertyTypes.AppTime:
                return;

            case PropertyTypes.Guid:
                AutocompleteStream.Take(bytes, ref offset, 16);
                return;

            case PropertyTypes.String8:
            case PropertyTypes.Unicode:
            case PropertyTypes.Binary:
                SkipCountedRun(bytes, ref offset);
                return;

            case PropertyTypes.MultipleString8:
            case PropertyTypes.MultipleUnicode:
            case PropertyTypes.MultipleBinary:
            {
                var count = AutocompleteStream.ReadUInt32(bytes, ref offset);
                for (var i = 0u; i < count; i++)
                    SkipCountedRun(bytes, ref offset);
                return;
            }

            case PropertyTypes.MultipleGuid:
            {
                var count = AutocompleteStream.ReadUInt32(bytes, ref offset);
                AutocompleteStream.Take(bytes, ref offset, checked((int)count * 16));
                return;
            }

            default:
                // An unknown type means the parser no longer knows where the next property starts,
                // and guessing would silently corrupt everything after it.
                throw new AutocompleteFormatException($"The autocomplete stream carries a property of type 0x{type:X4}, which this reader does not know the length of.");
        }
    }

    private static void SkipCountedRun(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var length = AutocompleteStream.ReadUInt32(bytes, ref offset);
        AutocompleteStream.Take(bytes, ref offset, checked((int)length));
    }

    /// <summary>The text of a string property, without the terminator the format stores.</summary>
    public string? AsString()
    {
        if (ValueData.Length < 4)
            return null;

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(ValueData);

        if (length <= 0 || 4 + length > ValueData.Length)
            return string.Empty;

        var text = Type == PropertyTypes.Unicode
            ? Encoding.Unicode.GetString(ValueData, 4, length)
            : Encoding.ASCII.GetString(ValueData, 4, length);

        return text.TrimEnd('\0');
    }

    public int AsInt32() => unchecked((int)(uint)Union);

    internal void SetInt32(int value) => Union = (uint)value;

    /// <summary>A string property, in the shape the format wants: length, bytes, terminator.</summary>
    public static AutocompleteProperty Unicode(uint tag, string value)
    {
        var text = Encoding.Unicode.GetBytes((value ?? string.Empty) + '\0');
        var data = new byte[4 + text.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)text.Length);
        text.CopyTo(data, 4);

        return new AutocompleteProperty(tag, 0, 0, data);
    }

    public static AutocompleteProperty Int32(uint tag, int value)
        => new(tag, 0, (uint)value, []);
}
