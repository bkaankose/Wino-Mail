using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// Message mutations and attachment access (MS-OXCROPS 2.2.6 / 2.2.7 / MS-OXCFOLD / MS-OXCMSG):
/// read flags, property writes with save, move/copy, delete, and the attachment table. These are the
/// write half of rung 4 and the attachment half of rung 3 of the plan.
///
/// A folder-level read-flag change and a move/delete take message ids directly, so none of them need
/// the message opened first. A property change (flag, categories) does: RopOpenMessage read/write,
/// RopSetProperties, RopSaveChangesMessage.
/// </summary>
public static class RopMessageOps
{
    public const byte RopSetProperties = 0x0A;
    public const byte RopSaveChangesMessage = 0x0C;
    public const byte RopDeleteMessages = 0x1E;
    public const byte RopGetAttachmentTable = 0x21;
    public const byte RopOpenAttachment = 0x22;
    public const byte RopMoveCopyMessages = 0x33;
    public const byte RopSetReadFlags = 0x66;

    /// <summary>OpenModeFlags for RopOpenMessage when a write follows.</summary>
    public const byte OpenReadWrite = 0x01;

    [Flags]
    public enum ReadFlags : byte
    {
        /// <summary>Mark read.</summary>
        Default = 0x00,
        /// <summary>Suppress read receipts.</summary>
        SuppressReceipt = 0x01,
        /// <summary>Mark unread.</summary>
        ClearReadFlag = 0x04,
        GenerateReceiptOnly = 0x10,
        ClearNotifyRead = 0x20,
        ClearNotifyUnread = 0x40,
    }

    /// <summary>RopSetReadFlags (MS-OXCROPS 2.2.6.10): on a FOLDER handle, for a set of message ids.</summary>
    public static byte[] BuildSetReadFlags(byte folderHandleIndex, ReadFlags flags, IReadOnlyList<ulong> messageIds)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSetReadFlags);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(folderHandleIndex);
        rop.UInt8(0);                    // WantAsynchronous
        rop.UInt8((byte)flags);
        rop.UInt16((ushort)messageIds.Count);
        foreach (var id in messageIds)
        {
            rop.UInt64(id);
        }

        return rop.ToArray();
    }

    /// <summary>Returns PartialCompletion: true when not every message was updated.</summary>
    public static bool ParseSetReadFlags(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopSetReadFlags, nameof(RopSetReadFlags));
        return reader.UInt8() != 0;
    }

    /// <summary>
    /// RopMoveCopyMessages (MS-OXCROPS 2.2.4.6): source and destination FOLDER handles, message ids.
    /// The moved messages keep their ids only when source and destination share a database; a move
    /// across replicas assigns new ones, which is why the caller re-syncs the destination afterwards.
    /// </summary>
    public static byte[] BuildMoveCopyMessages(byte sourceFolderHandleIndex, byte destinationFolderHandleIndex, IReadOnlyList<ulong> messageIds, bool copy)
    {
        var rop = new RopWriter();
        rop.UInt8(RopMoveCopyMessages);
        rop.UInt8(0);
        rop.UInt8(sourceFolderHandleIndex);
        rop.UInt8(destinationFolderHandleIndex);
        rop.UInt16((ushort)messageIds.Count);
        foreach (var id in messageIds)
        {
            rop.UInt64(id);
        }

        rop.UInt8(0);                    // WantAsynchronous
        rop.UInt8(copy ? (byte)1 : (byte)0);
        return rop.ToArray();
    }

    public static bool ParseMoveCopyMessages(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopMoveCopyMessages, nameof(RopMoveCopyMessages));
        return reader.UInt8() != 0;      // PartialCompletion
    }

    /// <summary>
    /// RopDeleteMessages (MS-OXCROPS 2.2.4.11): a HARD delete of the ids from the folder. A soft delete
    /// (to Deleted Items) is a move; the caller chooses.
    /// </summary>
    public static byte[] BuildDeleteMessages(byte folderHandleIndex, IReadOnlyList<ulong> messageIds)
    {
        var rop = new RopWriter();
        rop.UInt8(RopDeleteMessages);
        rop.UInt8(0);
        rop.UInt8(folderHandleIndex);
        rop.UInt8(0);                    // WantAsynchronous
        rop.UInt8(0);                    // NotifyNonRead: no non-read receipts
        rop.UInt16((ushort)messageIds.Count);
        foreach (var id in messageIds)
        {
            rop.UInt64(id);
        }

        return rop.ToArray();
    }

    public static bool ParseDeleteMessages(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopDeleteMessages, nameof(RopDeleteMessages));
        return reader.UInt8() != 0;      // PartialCompletion
    }

    /// <summary>RopSetProperties (MS-OXCROPS 2.2.8.6) on an open message (or folder, attachment).</summary>
    public static byte[] BuildSetProperties(byte handleIndex, IReadOnlyList<TaggedPropertyValue> values)
    {
        var body = new RopWriter();
        body.UInt16((ushort)values.Count);
        foreach (var value in values)
        {
            value.WriteTo(body);
        }

        var payload = body.ToArray();

        var rop = new RopWriter();
        rop.UInt8(RopSetProperties);
        rop.UInt8(0);
        rop.UInt8(handleIndex);
        rop.UInt16((ushort)payload.Length);   // PropertyValueSize: count field + values
        rop.Bytes(payload);
        return rop.ToArray();
    }

    /// <summary>Returns the property tags the server refused, with their error codes.</summary>
    public static List<(uint Tag, uint Error)> ParseSetProperties(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopSetProperties, nameof(RopSetProperties));
        var problemCount = reader.UInt16();
        var problems = new List<(uint, uint)>(problemCount);
        for (var i = 0; i < problemCount; i++)
        {
            reader.UInt16();             // Index into the request
            var tag = reader.UInt32();
            var error = reader.UInt32();
            problems.Add((tag, error));
        }

        return problems;
    }

    [Flags]
    public enum SaveFlags : byte
    {
        KeepOpenReadOnly = 0x01,
        KeepOpenReadWrite = 0x02,
        ForceSave = 0x04,
    }

    /// <summary>RopSaveChangesMessage (MS-OXCROPS 2.2.6.3). The response handle index is a second slot the ROP reports on.</summary>
    public static byte[] BuildSaveChangesMessage(byte responseHandleIndex, byte messageHandleIndex, SaveFlags flags = SaveFlags.ForceSave)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSaveChangesMessage);
        rop.UInt8(0);
        rop.UInt8(responseHandleIndex);
        rop.UInt8(messageHandleIndex);
        rop.UInt8((byte)flags);
        return rop.ToArray();
    }

    /// <summary>Returns the saved message's id (a new message gets its id here).</summary>
    public static ulong ParseSaveChangesMessage(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopSaveChangesMessage, nameof(RopSaveChangesMessage));
        reader.UInt8();                  // InputHandleIndex echoed
        return reader.UInt64();
    }

    /// <summary>RopGetAttachmentTable (MS-OXCROPS 2.2.6.13) on an open message.</summary>
    public static byte[] BuildGetAttachmentTable(byte messageHandleIndex, byte outputHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopGetAttachmentTable);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8(0x00);                 // TableFlags: Standard (0x40 = Unicode)
        return rop.ToArray();
    }

    public static void ParseGetAttachmentTable(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopGetAttachmentTable, nameof(RopGetAttachmentTable));

    /// <summary>RopOpenAttachment (MS-OXCROPS 2.2.6.12) by PidTagAttachNumber, read-only.</summary>
    public static byte[] BuildOpenAttachment(byte messageHandleIndex, byte outputHandleIndex, uint attachmentNumber)
    {
        var rop = new RopWriter();
        rop.UInt8(RopOpenAttachment);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8(0x00);                 // OpenAttachmentFlags: ReadOnly
        rop.UInt32(attachmentNumber);
        return rop.ToArray();
    }

    public static void ParseOpenAttachment(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopOpenAttachment, nameof(RopOpenAttachment));
}

/// <summary>A property tag with a value, encoded as TaggedPropertyValue (MS-OXCDATA 2.11.4) for writes.</summary>
public sealed record TaggedPropertyValue(uint Tag, object Value)
{
    public static TaggedPropertyValue Long(uint tag, uint value) => new(tag, value);
    public static TaggedPropertyValue Boolean(uint tag, bool value) => new(tag, value);
    public static TaggedPropertyValue Unicode(uint tag, string value) => new(tag, value);
    public static TaggedPropertyValue Binary(uint tag, byte[] value) => new(tag, value);
    public static TaggedPropertyValue SysTime(uint tag, DateTime utc) => new(tag, utc);

    public void WriteTo(RopWriter writer)
    {
        writer.UInt32(Tag);
        switch ((ushort)(Tag & 0xFFFF), Value)
        {
            case (PropertyTypes.Short, ushort v): writer.UInt16(v); break;
            case (PropertyTypes.Long, uint v): writer.UInt32(v); break;
            case (PropertyTypes.Long, int v): writer.UInt32(unchecked((uint)v)); break;
            case (PropertyTypes.Double, double v): writer.UInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v))); break;
            case (PropertyTypes.Boolean, bool v): writer.UInt8(v ? (byte)1 : (byte)0); break;
            case (PropertyTypes.LongLong, ulong v): writer.UInt64(v); break;
            case (PropertyTypes.Unicode, string v): writer.UnicodeZ(v); break;
            case (PropertyTypes.String8, string v): writer.AsciiZ(v); break;
            case (PropertyTypes.SysTime, DateTime v): writer.UInt64(unchecked((ulong)v.ToUniversalTime().ToFileTimeUtc())); break;
            case (PropertyTypes.Binary or PropertyTypes.ServerId, byte[] v):   // PtypServerId is a counted binary (MS-OXCDATA 2.11.1.4)
                writer.UInt16((ushort)v.Length);
                writer.Bytes(v);
                break;
            case (PropertyTypes.MultipleUnicode, string[] v):
                writer.UInt16((ushort)v.Length);
                foreach (var item in v)
                {
                    writer.UnicodeZ(item);
                }
                break;
            case (PropertyTypes.Restriction, Restrictions.RestrictionNode v):
                Restrictions.Restriction.Encode(writer, v, extendedFormat: false);
                break;
            case (PropertyTypes.RuleAction, IReadOnlyList<Rules.RuleAction> v):
                Rules.RuleActions.Write(writer, v);
                break;
            default:
                throw new MapiFormatException($"Cannot encode a {Value?.GetType().Name ?? "null"} as property type 0x{Tag & 0xFFFF:X4}.");
        }
    }
}
