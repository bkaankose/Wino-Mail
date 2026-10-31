using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// Id conversion ROPs (MS-OXCROPS 2.2.11 / MS-OXCDATA 2.2.1.3). A folder or message id is only
/// meaningful within one logon: it is a 16-bit replica id plus a 48-bit counter. The long-term id
/// replaces the replica id with the database GUID it stands for, which is stable across sessions,
/// is what ICS state carries, and is what an EntryId is built from.
/// </summary>
public static class RopIds
{
    public const byte RopLongTermIdFromId = 0x43;
    public const byte RopIdFromLongTermId = 0x44;

    /// <summary>RopIdFromLongTermId (MS-OXCROPS 2.2.11.1): the inverse, giving this logon's short id for a long-term id.</summary>
    public static byte[] BuildIdFromLongTermId(byte logonHandleIndex, ReadOnlySpan<byte> longTermId)
    {
        if (longTermId.Length != LongTermIdLength)
        {
            throw new ArgumentException($"A long-term id is {LongTermIdLength} bytes.", nameof(longTermId));
        }

        var rop = new RopWriter();
        rop.UInt8(RopIdFromLongTermId);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(logonHandleIndex);
        rop.Bytes(longTermId);
        rop.UInt16(0);                   // Padding
        return rop.ToArray();
    }

    /// <summary>Returns the folder or message id.</summary>
    public static ulong ParseIdFromLongTermId(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopIdFromLongTermId, nameof(RopIdFromLongTermId));
        return reader.UInt64();
    }

    /// <summary>Several inverse conversions in one Execute, all on the logon handle.</summary>
    public static byte[] BuildIdFromLongTermIdBatch(byte logonHandleIndex, IReadOnlyList<byte[]> longTermIds)
    {
        var rop = new RopWriter();
        foreach (var id in longTermIds)
            rop.Bytes(BuildIdFromLongTermId(logonHandleIndex, id));
        return rop.ToArray();
    }

    /// <summary>Parses a batch's responses in request order; the list is shorter if the server stopped at a failure.</summary>
    public static List<ulong> ParseIdFromLongTermIdBatch(byte[] rops, int expected)
    {
        var reader = new RopReader(rops);
        var results = new List<ulong>(expected);
        while (results.Count < expected && reader.Remaining >= 6)
            results.Add(ParseIdFromLongTermId(reader));
        return results;
    }

    /// <summary>
    /// Pulls the message's long-term id out of a Message EntryID (MS-OXCDATA 2.2.4.2): after Flags,
    /// ProviderUID and MessageType come the folder's DatabaseGuid + GlobalCounter + pad, then the
    /// message's own DatabaseGuid + GlobalCounter + pad. 70 bytes in all.
    /// </summary>
    public static byte[] MessageLongTermIdFromEntryId(ReadOnlySpan<byte> entryId)
    {
        if (entryId.Length < 70)
        {
            throw new MapiFormatException($"A message EntryID is 70 bytes; got {entryId.Length}.");
        }

        return entryId.Slice(46, LongTermIdLength).ToArray();
    }

    /// <summary>DatabaseGuid (16) + GlobalCounter (6); the 2 padding bytes on the wire are dropped.</summary>
    public const int LongTermIdLength = 22;

    public static byte[] BuildLongTermIdFromId(byte logonHandleIndex, ulong objectId)
    {
        var rop = new RopWriter();
        rop.UInt8(RopLongTermIdFromId);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(logonHandleIndex);
        rop.UInt64(objectId);
        return rop.ToArray();
    }

    /// <summary>
    /// Several conversions in one Execute: the ROPs are independent, all on the logon handle, and tiny,
    /// so batching costs nothing and saves a round trip per folder.
    /// </summary>
    public static byte[] BuildLongTermIdFromIdBatch(byte logonHandleIndex, IReadOnlyList<ulong> objectIds)
    {
        var rop = new RopWriter();
        foreach (var id in objectIds)
        {
            rop.Bytes(BuildLongTermIdFromId(logonHandleIndex, id));
        }

        return rop.ToArray();
    }

    /// <summary>Parses one response; returns the 22-byte long-term id.</summary>
    public static byte[] ParseLongTermIdFromId(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopLongTermIdFromId, nameof(RopLongTermIdFromId));
        var longTermId = reader.Bytes(LongTermIdLength).ToArray();
        reader.UInt16();                 // Padding
        return longTermId;
    }

    /// <summary>
    /// Parses the responses of a batch, in request order. A server stops processing a batch at the
    /// first failing ROP (MS-OXCROPS 3.2.5.1), so the list may be shorter than the request; the
    /// caller decides what to do with the missing tail.
    /// </summary>
    public static List<byte[]> ParseLongTermIdFromIdBatch(byte[] rops, int expected)
    {
        var reader = new RopReader(rops);
        var results = new List<byte[]>(expected);
        while (results.Count < expected && reader.Remaining >= 6)
        {
            results.Add(ParseLongTermIdFromId(reader));
        }

        return results;
    }

    /// <summary>
    /// Folder EntryID (MS-OXCDATA 2.2.4.1): Flags 0, the ProviderUID (for a private mailbox, the
    /// MailboxGuid from the logon), FolderType 0x0001 (private folder), then the long-term id's
    /// DatabaseGuid and GlobalCounter, then 2 bytes of padding. 46 bytes, the same shape Exchange
    /// itself returns in PidTagIpmDraftsEntryId and friends, so the two can be compared byte for byte.
    /// </summary>
    public static byte[] BuildPrivateFolderEntryId(Guid mailboxGuid, ReadOnlySpan<byte> longTermId)
    {
        if (longTermId.Length != LongTermIdLength)
        {
            throw new ArgumentException($"A long-term id is {LongTermIdLength} bytes.", nameof(longTermId));
        }

        var writer = new RopWriter();
        writer.UInt32(0);                            // Flags
        writer.Bytes(mailboxGuid.ToByteArray());     // ProviderUID
        writer.UInt16(0x0001);                       // FolderType: private folder
        writer.Bytes(longTermId);                    // DatabaseGuid + GlobalCounter
        writer.UInt16(0);                            // Pad
        return writer.ToArray();
    }
}
