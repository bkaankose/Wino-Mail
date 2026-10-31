using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// The message and stream ROPs that turn a table row into property bytes:
/// RopOpenMessage, RopOpenStream, RopReadStream (repeat), RopRelease.
///
/// A rule condition is tens of kilobytes, well past the ~8KB a RopGetPropertiesSpecific will return
/// inline (it answers with PT_ERROR NotEnoughMemory instead), so the stream path is not an
/// optimisation here, it is the only way to get the property at all.
/// </summary>
public static class RopMessage
{
    public const byte RopRelease = 0x01;
    public const byte RopOpenMessage = 0x03;
    public const byte RopOpenStream = 0x2B;
    public const byte RopReadStream = 0x2C;

    /// <summary>MS-OXCROPS 2.2.9.2: a ByteCount of 0xBABE means "MaximumByteCount follows as a uint32".</summary>
    public const ushort ByteCountEscape = 0xBABE;

    /// <summary>
    /// The largest RopReadStream request that reliably fits the 32KB ROP response buffer with its
    /// headers. Asking for 32KB fails the whole Execute with ecBufferTooSmall.
    /// </summary>
    public const uint SafeReadChunk = 24 * 1024;

    /// <param name="openModeFlags">0x00 read-only (default), 0x01 read/write, 0x03 best access.</param>
    public static byte[] BuildOpenMessage(ulong folderId, ulong messageId, byte inputHandleIndex, byte outputHandleIndex, byte openModeFlags = 0x00)
    {
        var rop = new RopWriter();
        rop.UInt8(RopOpenMessage);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(inputHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt16(0x0FFF);              // CodePageId: use the logon's
        rop.UInt64(folderId);
        rop.UInt8(openModeFlags);
        rop.UInt64(messageId);
        return rop.ToArray();
    }

    /// <summary>
    /// Only the return value matters here. The rest of the response (HasNamedProperties, subject
    /// prefix and normalized subject as TypedStrings, then the recipient table) is left unread on
    /// purpose: the ROP is the last one in its Execute, so nothing after it depends on the position.
    /// </summary>
    public static void ParseOpenMessage(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopOpenMessage, nameof(RopOpenMessage));

    /// <summary>The part of the RopOpenMessage response this client uses: the recipient table it carries.</summary>
    public sealed record OpenMessageResponse(bool HasNamedProperties, string? NormalizedSubject, ushort RecipientCount, List<uint> RecipientColumns, List<OpenRecipient> Recipients);

    /// <summary>
    /// RopOpenMessage success response (MS-OXCROPS 2.2.6.1.2): HasNamedProperties, SubjectPrefix and
    /// NormalizedSubject as TypedStrings, RecipientCount, RecipientColumnCount + columns, RowCount, then
    /// OpenRecipientRows (RecipientType, CodePageId, Reserved, RecipientRowSize, RecipientRow). Rows are
    /// length-delimited, so one the client cannot decode is dropped rather than failing the response.
    /// </summary>
    public static OpenMessageResponse ParseOpenMessageResponse(RopReader reader, Action<string>? diagnostics = null)
    {
        RopExecute.ExpectSuccess(reader, RopOpenMessage, nameof(RopOpenMessage));
        var hasNamedProperties = reader.UInt8() != 0;
        ReadTypedString(reader);                             // SubjectPrefix
        var normalizedSubject = ReadTypedString(reader);
        var recipientCount = reader.UInt16();
        var columnCount = reader.UInt16();
        var columns = new List<uint>(columnCount);
        for (var i = 0; i < columnCount; i++) columns.Add(reader.UInt32());

        var rowCount = reader.UInt8();
        var recipients = new List<OpenRecipient>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            var recipientType = reader.UInt8();
            reader.UInt16();                                 // CodePageId
            reader.UInt16();                                 // Reserved
            var size = reader.UInt16();
            var row = reader.Bytes(size);
            try
            {
                recipients.Add(RecipientRow.Parse(recipientType, row, columns));
            }
            catch (MapiFormatException ex)
            {
                diagnostics?.Invoke($"recipient row {i} not decoded ({ex.Message})");
            }
        }

        return new OpenMessageResponse(hasNamedProperties, normalizedSubject, recipientCount, columns, recipients);
    }

    /// <summary>TypedString (MS-OXCDATA 2.11.7): StringType then a string in that encoding, or nothing.</summary>
    private static string? ReadTypedString(RopReader reader)
    {
        var type = reader.UInt8();
        return type switch
        {
            0x00 => null,
            0x01 => string.Empty,
            0x02 => reader.AsciiZ(),                         // String8
            0x03 => reader.AsciiZ(),                         // UnicodeReduced: one byte per character (seen live for NormalizedSubject)
            0x04 => reader.UnicodeZ(),                       // Unicode
            _ => throw new MapiFormatException($"Unhandled TypedString type 0x{type:X2}."),
        };
    }

    /// <param name="openModeFlags">0x00 read-only (default), 0x01 read/write, 0x02 create (replace), 0x03 best access.</param>
    public static byte[] BuildOpenStream(uint propertyTag, byte inputHandleIndex, byte outputHandleIndex, byte openModeFlags = 0x00)
    {
        var rop = new RopWriter();
        rop.UInt8(RopOpenStream);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt32(propertyTag);
        rop.UInt8(openModeFlags);
        return rop.ToArray();
    }

    /// <summary>Returns StreamSize, the full length of the property.</summary>
    public static uint ParseOpenStream(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopOpenStream, nameof(RopOpenStream));
        return reader.UInt32();
    }

    public static byte[] BuildReadStream(byte inputHandleIndex, uint byteCount)
    {
        var rop = new RopWriter();
        rop.UInt8(RopReadStream);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);

        if (byteCount < ByteCountEscape)
        {
            rop.UInt16((ushort)byteCount);
        }
        else
        {
            rop.UInt16(ByteCountEscape);
            rop.UInt32(byteCount);       // MaximumByteCount
        }

        return rop.ToArray();
    }

    /// <summary>Returns the bytes read; empty at end of stream.</summary>
    public static byte[] ParseReadStream(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopReadStream, nameof(RopReadStream));
        var size = reader.UInt16();
        return reader.Bytes(size).ToArray();
    }

    /// <summary>RopRelease has no response buffer; the handle slot simply stops being valid.</summary>
    public static byte[] BuildRelease(byte inputHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopRelease);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        return rop.ToArray();
    }
}
