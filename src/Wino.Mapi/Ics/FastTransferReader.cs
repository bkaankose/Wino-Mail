using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Ics;

/// <summary>Well-known markers and meta-properties of a FastTransfer stream (MS-OXCFXICS 2.2.4.1.4 / 2.2.4.1.5).</summary>
public static class FxTags
{
    public const uint StartTopFld = 0x40090003;
    public const uint StartSubFld = 0x400A0003;
    public const uint EndFolder = 0x400B0003;
    public const uint StartMessage = 0x400C0003;
    public const uint EndMessage = 0x400D0003;
    public const uint StartFAIMsg = 0x40100003;
    public const uint StartEmbed = 0x40010003;
    public const uint EndEmbed = 0x40020003;
    public const uint StartRecip = 0x40030003;
    public const uint EndToRecip = 0x40040003;
    public const uint NewAttach = 0x40000003;
    public const uint EndAttach = 0x400E0003;
    public const uint IncrSyncChg = 0x40120003;
    public const uint IncrSyncChgPartial = 0x407D0003;
    public const uint IncrSyncDel = 0x40130003;
    public const uint IncrSyncEnd = 0x40140003;
    public const uint IncrSyncMessage = 0x40150003;
    public const uint IncrSyncRead = 0x402F0003;
    public const uint IncrSyncStateBegin = 0x403A0003;
    public const uint IncrSyncStateEnd = 0x403B0003;
    public const uint IncrSyncProgressMode = 0x4074000B;
    public const uint IncrSyncProgressPerMsg = 0x4075000B;
    public const uint IncrSyncGroupInfo = 0x407B0102;
    public const uint FXErrorInfo = 0x40180003;

    public const uint MetaTagEcWarning = 0x400F0003;
    public const uint MetaTagNewFXFolder = 0x40110102;
    public const uint MetaTagIncrSyncGroupId = 0x407C0003;
    public const uint MetaTagIncrementalSyncMessagePartial = 0x407A0003;
    public const uint MetaTagDnPrefix = 0x4008001E;

    /// <summary>Typed PtypInteger32 by tag, but its value on the wire is a counted IDSET (special-cased in the reader).</summary>
    public const uint MetaTagIdsetGiven = 0x40170003;
    public const uint MetaTagCnsetSeen = 0x67960102;
    public const uint MetaTagCnsetSeenFAI = 0x67DA0102;
    public const uint MetaTagCnsetRead = 0x67D20102;
    public const uint MetaTagIdsetDeleted = 0x67E50102;
    public const uint MetaTagIdsetNoLongerInScope = 0x40210102;
    public const uint MetaTagIdsetExpired = 0x40220102;
    public const uint MetaTagIdsetRead = 0x402D0102;
    public const uint MetaTagIdsetUnread = 0x402E0102;

    /// <summary>The four state properties a client carries between syncs.</summary>
    public static readonly uint[] StateProperties = [MetaTagIdsetGiven, MetaTagCnsetSeen, MetaTagCnsetSeenFAI, MetaTagCnsetRead];

    public static bool IsMarker(uint tag) => tag is
        StartTopFld or StartSubFld or EndFolder or StartMessage or EndMessage or StartFAIMsg or StartEmbed or EndEmbed
        or StartRecip or EndToRecip or NewAttach or EndAttach or IncrSyncChg or IncrSyncChgPartial or IncrSyncDel
        or IncrSyncEnd or IncrSyncMessage or IncrSyncRead or IncrSyncStateBegin or IncrSyncStateEnd
        or IncrSyncProgressMode or IncrSyncProgressPerMsg or FXErrorInfo;
}

/// <summary>One element of a FastTransfer stream: a marker, or a property with its value.</summary>
public readonly record struct FxElement(uint Tag, object? Value)
{
    public bool IsMarker => FxTags.IsMarker(Tag);
}

/// <summary>
/// Reads the element encoding of a FastTransfer stream (MS-OXCFXICS 2.2.4.1). It is the PropertyRow
/// encoding's cousin with every count widened to 32 bits and strings length-prefixed rather than
/// terminated; markers are property tags of type 0x0003 whose id is reserved. The reader is fed
/// buffer by buffer as RopFastTransferSourceGetBuffer returns them and never assumes an element
/// ends where a buffer does: a partial element at the tail is carried into the next buffer.
/// </summary>
public sealed class FastTransferReader
{
    private byte[] _pending = [];

    /// <summary>Appends a transfer buffer and yields every element that is now complete.</summary>
    public List<FxElement> Feed(ReadOnlySpan<byte> buffer)
    {
        var data = new byte[_pending.Length + buffer.Length];
        _pending.CopyTo(data, 0);
        buffer.CopyTo(data.AsSpan(_pending.Length));

        var elements = new List<FxElement>();
        var reader = new RopReader(data);
        var consumed = 0;

        while (reader.Remaining >= 4)
        {
            try
            {
                elements.Add(ReadElement(reader));
                consumed = reader.Position;
            }
            catch (MapiFormatException)
            {
                // A buffer underrun here means the element continues in the next buffer; the bytes
                // consumed so far are rewound to `consumed` and carried over.
                break;
            }
        }

        _pending = data[consumed..];
        return elements;
    }

    public int PendingBytes => _pending.Length;

    private static FxElement ReadElement(RopReader reader)
    {
        var tag = reader.UInt32();
        if (FxTags.IsMarker(tag))
            return new FxElement(tag, null);

        var type = (ushort)(tag & 0xFFFF);
        var id = (ushort)(tag >> 16);

        if (id >= 0x8000)
        {
            // Named property: GUID, kind, then a dispatch id or a name. Not expected with the
            // property sets this client asks for; read it so the stream stays aligned.
            reader.Bytes(16);
            var kind = reader.UInt8();
            if (kind == 0)
                reader.UInt32();
            else
                ReadLengthPrefixedUnicode(reader);
        }

        object? value = tag switch
        {
            FxTags.MetaTagIdsetGiven => ReadCountedBinary(reader),
            _ => ReadValue(reader, type),
        };

        return new FxElement(tag, value);
    }

    private static object? ReadValue(RopReader reader, ushort type) => type switch
    {
        PropertyTypes.Short => reader.UInt16(),
        PropertyTypes.Long => reader.UInt32(),
        PropertyTypes.ErrorCode => reader.UInt32(),
        PropertyTypes.Boolean => reader.UInt16() != 0,                    // 2 bytes in a FastTransfer stream
        PropertyTypes.LongLong => reader.UInt64(),
        PropertyTypes.SysTime => DateTime.FromFileTimeUtc(unchecked((long)reader.UInt64())),
        0x0005 => BitConverter.Int64BitsToDouble(unchecked((long)reader.UInt64())),   // PtypFloating64
        0x0007 => BitConverter.Int64BitsToDouble(unchecked((long)reader.UInt64())),   // PtypFloatingTime
        PropertyTypes.Guid => new Guid(reader.Bytes(16).Span),
        PropertyTypes.Unicode => ReadLengthPrefixedUnicode(reader),
        PropertyTypes.String8 => ReadLengthPrefixedString8(reader),
        PropertyTypes.Binary => ReadCountedBinary(reader),
        PropertyTypes.Object => ReadCountedBinary(reader),
        0x1003 => ReadMultiple(reader, r => r.UInt32()),                  // PtypMultipleInteger32
        0x1014 => ReadMultiple(reader, r => r.UInt64()),
        0x101F => ReadMultiple(reader, ReadLengthPrefixedUnicode),
        0x101E => ReadMultiple(reader, ReadLengthPrefixedString8),
        PropertyTypes.MultipleBinary => ReadMultiple(reader, ReadCountedBinary),
        _ => throw new MapiFormatException($"Unhandled FastTransfer property type 0x{type:X4}."),
    };

    private static byte[] ReadCountedBinary(RopReader reader)
    {
        var count = reader.UInt32();
        if (count > 256 * 1024 * 1024)
            throw new MapiFormatException($"FastTransfer binary of {count} bytes is implausible; the stream is misread.");
        return reader.Bytes((int)count).ToArray();
    }

    private static string ReadLengthPrefixedUnicode(RopReader reader)
    {
        var count = reader.UInt32();
        var bytes = reader.Bytes((int)count).Span;
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static string ReadLengthPrefixedString8(RopReader reader)
    {
        var count = reader.UInt32();
        var bytes = reader.Bytes((int)count).Span;
        return Encoding.Latin1.GetString(bytes).TrimEnd('\0');
    }

    private static object[] ReadMultiple<T>(RopReader reader, Func<RopReader, T> readOne) where T : notnull
    {
        var count = reader.UInt32();
        if (count > 1_000_000)
            throw new MapiFormatException("FastTransfer multi-value count is implausible; the stream is misread.");
        var values = new object[count];
        for (var i = 0; i < count; i++)
            values[i] = readOne(reader);
        return values;
    }
}
