using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// Server notifications (MS-OXCNOTIF): RopRegisterNotification subscribes a logon to store events;
/// pending events arrive as RopNotify responses appended to any later Execute's ROP response buffer,
/// and the MAPI/HTTP NotificationWait request is the long-poll that says when to send that Execute.
/// </summary>
public static class RopNotify
{
    public const byte RopRegisterNotification = 0x29;
    public const byte RopId = 0x2A;
    public const byte RopPending = 0x6E;

    [Flags]
    public enum NotificationTypes : ushort
    {
        CriticalError = 0x0001,
        NewMail = 0x0002,
        ObjectCreated = 0x0004,
        ObjectDeleted = 0x0008,
        ObjectModified = 0x0010,
        ObjectMoved = 0x0020,
        ObjectCopied = 0x0040,
        SearchCompleted = 0x0080,
        TableModified = 0x0100,
        Extended = 0x0400,

        /// <summary>Everything a mail client cares about for keeping folders current.</summary>
        StoreChanges = NewMail | ObjectCreated | ObjectDeleted | ObjectModified | ObjectMoved | ObjectCopied,
    }

    /// <summary>
    /// RopRegisterNotification (MS-OXCROPS 2.2.14.1) on the LOGON handle. WantWholeStore = 1 with zero
    /// ids subscribes to every folder of the mailbox; that is one subscription per account.
    /// </summary>
    public static byte[] BuildRegisterNotification(byte logonHandleIndex, byte outputHandleIndex, NotificationTypes types, bool wantWholeStore = true, ulong folderId = 0, ulong messageId = 0)
    {
        var rop = new RopWriter();
        rop.UInt8(RopRegisterNotification);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(logonHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt16((ushort)types);       // NotificationTypes

        // Reserved (1 byte) is present ONLY when the TableModified bit is set (MS-OXCROPS 2.2.14.1.1).
        // Sending it unconditionally made the request one byte long and Exchange refused the Execute.
        if ((types & NotificationTypes.TableModified) != 0)
            rop.UInt8(0);

        rop.UInt8(wantWholeStore ? (byte)1 : (byte)0);
        if (!wantWholeStore)
        {
            rop.UInt64(folderId);
            rop.UInt64(messageId);
        }

        return rop.ToArray();
    }

    public static void ParseRegisterNotification(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopRegisterNotification, nameof(RopRegisterNotification));

    /// <summary>One decoded notification. Ids are this logon's short ids.</summary>
    public sealed record Notification(NotificationTypes Type, bool IsMessage, ulong? FolderId, ulong? MessageId, ulong? ParentFolderId, bool IsTable);

    /// <summary>
    /// Reads every RopNotify (and skips RopPending) in a ROP response buffer. Any other ROP stops the
    /// scan: the caller sends notification-collecting Executes with no ROPs of its own, so anything
    /// else means the buffer is not what was expected.
    /// </summary>
    public static List<Notification> ParseNotifications(byte[] rops)
    {
        var reader = new RopReader(rops);
        var notifications = new List<Notification>();

        while (reader.Remaining >= 1)
        {
            var ropId = reader.UInt8();
            if (ropId == RopPending)
            {
                reader.UInt16();         // SessionIndex
                continue;
            }

            if (ropId != RopId)
                break;

            reader.UInt32();             // NotificationHandle
            reader.UInt8();              // LogonId
            notifications.Add(ReadNotificationData(reader));
        }

        return notifications;
    }

    /// <summary>NotificationData (MS-OXCNOTIF 2.2.1.4.1), the part that varies with the flags.</summary>
    private static Notification ReadNotificationData(RopReader reader)
    {
        var flags = reader.UInt16();
        var type = (NotificationTypes)(flags & 0x0FFF);
        var isTable = (flags & 0x1000) != 0;
        var isUnicode = (flags & 0x2000) != 0;
        var isSearch = (flags & 0x4000) != 0;
        var isMessage = (flags & 0x8000) != 0;

        if (type == NotificationTypes.Extended)
        {
            // Extended notifications are opaque; consume and report nothing.
            return new Notification(type, false, null, null, null, false);
        }

        if (isTable)
        {
            // Table notifications describe row changes of a table object; a whole-store subscription does
            // not produce them, but be safe: TableEventType then a shape that depends on it. Only the
            // shapes without row data are decoded; anything else ends the scan by throwing.
            var tableEvent = reader.UInt16();
            switch (tableEvent)
            {
                case 0x0001: case 0x0002: case 0x0007: return new Notification(type, false, null, null, null, true);   // TableChanged, TableError, TableRestrictionChanged
                case 0x0003: case 0x0004: case 0x0005: case 0x0006: case 0x0008:
                    var folder = reader.UInt64();
                    ulong? message = isMessage ? reader.UInt64() : null;
                    if (isMessage) reader.UInt32();               // Instance
                    if (tableEvent is 0x0003 or 0x0005)           // RowAdded / RowModified carry an insert-after position + row data
                        throw new MapiFormatException("Table row notifications are not decoded.");
                    return new Notification(type, isMessage, folder, message, null, true);
                default:
                    throw new MapiFormatException($"Unknown TableEventType 0x{tableEvent:X4}.");
            }
        }

        var folderId = reader.UInt64();
        ulong? messageId = isMessage ? reader.UInt64() : null;

        // ParentFolderId (MS-OXCNOTIF 2.2.1.4.1.2), decoded from a live capture rather than the prose:
        // for Created/Deleted/Moved/Copied it is present unless the event is a plain message event
        // (M set, S clear). A message created in a folder carries FolderId + MessageId then TagCount;
        // the same message seen through a search folder (S set) carries ParentFolderId too; a folder
        // event (M clear) always carries it.
        var hasParent = type is NotificationTypes.ObjectCreated or NotificationTypes.ObjectDeleted or NotificationTypes.ObjectMoved or NotificationTypes.ObjectCopied
                        && !(isMessage && !isSearch);

        ulong? parentFolderId = null;
        if (hasParent && type is NotificationTypes.ObjectCreated or NotificationTypes.ObjectDeleted)
            parentFolderId = reader.UInt64();

        if (type is NotificationTypes.ObjectMoved or NotificationTypes.ObjectCopied)
        {
            if (hasParent) parentFolderId = reader.UInt64();
            reader.UInt64();                                      // OldFolderId
            if (isMessage) reader.UInt64();                       // OldMessageId
            if (hasParent) reader.UInt64();                       // OldParentFolderId
        }

        if (type is NotificationTypes.ObjectCreated or NotificationTypes.ObjectModified)
        {
            // TagCount + PropertyTags: which properties changed. Not needed; skip.
            var tagCount = reader.UInt16();
            if (tagCount != 0xFFFF)
                reader.Bytes(tagCount * 4);
        }

        // A folder ObjectModified may carry TotalMessageCount / UnreadMessageCount (4 + 4). Their presence
        // is signalled in the flags, not by what remains in the buffer: guessing from the remainder ate the
        // head of the next notification when several arrived together. Decoded from a live capture: see
        // the listener's raw dump on parse failure. Until the flag is confirmed they are not read.

        if (type == NotificationTypes.NewMail)
        {
            reader.UInt32();                                      // MessageFlags
            var unicode = reader.UInt8() != 0;                    // UnicodeFlag
            if (unicode) reader.UnicodeZ(); else reader.AsciiZ(); // MessageClass
        }

        return new Notification(type, isMessage, folderId, messageId, parentFolderId, false);
    }
}
