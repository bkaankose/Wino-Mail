using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// Creating and sending messages (MS-OXCMSG, MS-OXOMSG, MS-OXCPRPT): RopCreateMessage,
/// RopModifyRecipients, attachments, write streams, RopSubmitMessage. The write half of the message
/// model; rung 5 of the plan.
/// </summary>
public static class RopMessageWrite
{
    public const byte RopCreateMessage = 0x06;
    public const byte RopRemoveAllRecipients = 0x0D;
    public const byte RopModifyRecipients = 0x0E;
    public const byte RopCreateAttachment = 0x23;
    public const byte RopDeleteAttachment = 0x24;
    public const byte RopOpenEmbeddedMessage = 0x46;
    public const byte RopSaveChangesAttachment = 0x25;
    public const byte RopWriteStream = 0x2D;
    public const byte RopSubmitMessage = 0x32;
    public const byte RopCommitStream = 0x5D;

    /// <summary>OpenModeFlags for RopOpenStream when writing: replace whatever the property held.</summary>
    public const byte OpenStreamCreate = 0x02;

    /// <summary>RopCreateMessage (MS-OXCROPS 2.2.6.2) on the LOGON handle, in the given folder.</summary>
    public static byte[] BuildCreateMessage(byte logonHandleIndex, byte outputHandleIndex, ulong folderId, bool associated = false)
    {
        var rop = new RopWriter();
        rop.UInt8(RopCreateMessage);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(logonHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt16(0x0FFF);              // CodePageId: the logon's
        rop.UInt64(folderId);
        rop.UInt8(associated ? (byte)1 : (byte)0);
        return rop.ToArray();
    }

    /// <summary>Returns the message id when the server assigned one at creation (it usually does).</summary>
    public static ulong? ParseCreateMessage(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopCreateMessage, nameof(RopCreateMessage));
        var hasMessageId = reader.UInt8() != 0;
        return hasMessageId ? reader.UInt64() : null;
    }

    /// <summary>RopCreateAttachment (MS-OXCROPS 2.2.6.13) on an open message.</summary>
    public static byte[] BuildCreateAttachment(byte messageHandleIndex, byte outputHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopCreateAttachment);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt8(outputHandleIndex);
        return rop.ToArray();
    }

    /// <summary>Returns the new AttachmentID.</summary>
    public static uint ParseCreateAttachment(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopCreateAttachment, nameof(RopCreateAttachment));
        return reader.UInt32();
    }

    public static byte[] BuildSaveChangesAttachment(byte responseHandleIndex, byte attachmentHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSaveChangesAttachment);
        rop.UInt8(0);
        rop.UInt8(responseHandleIndex);
        rop.UInt8(attachmentHandleIndex);
        rop.UInt8((byte)RopMessageOps.SaveFlags.ForceSave);
        return rop.ToArray();
    }

    public static void ParseSaveChangesAttachment(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopSaveChangesAttachment, nameof(RopSaveChangesAttachment));

    /// <summary>RopDeleteAttachment (MS-OXCROPS 2.2.6.14): removes the attachment with this PidTagAttachNumber from an open message.</summary>
    public static byte[] BuildDeleteAttachment(byte messageHandleIndex, uint attachmentId)
    {
        var rop = new RopWriter();
        rop.UInt8(RopDeleteAttachment);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt32(attachmentId);
        return rop.ToArray();
    }

    public static void ParseDeleteAttachment(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopDeleteAttachment, nameof(RopDeleteAttachment));

    /// <summary>
    /// RopOpenEmbeddedMessage (MS-OXCROPS 2.2.6.16): the message inside an attachment, created when
    /// <paramref name="create"/> (OpenModeFlags 0x02, read/write), else opened read/write (0x01).
    /// </summary>
    public static byte[] BuildOpenEmbeddedMessage(byte attachmentHandleIndex, byte outputHandleIndex, bool create)
    {
        var rop = new RopWriter();
        rop.UInt8(RopOpenEmbeddedMessage);
        rop.UInt8(0);
        rop.UInt8(attachmentHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt16(0x0FFF);                                  // CodePageId: the logon's
        rop.UInt8(create ? (byte)0x02 : (byte)0x01);
        return rop.ToArray();
    }

    /// <summary>Returns the embedded message's id; the rest of the response (the RopOpenMessage shape) is left unread.</summary>
    public static ulong ParseOpenEmbeddedMessage(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopOpenEmbeddedMessage, nameof(RopOpenEmbeddedMessage));
        reader.UInt8();                                      // Reserved
        return reader.UInt64();
    }

    /// <summary>RopWriteStream (MS-OXCROPS 2.2.9.3): one chunk; the response says how much was taken.</summary>
    public static byte[] BuildWriteStream(byte streamHandleIndex, ReadOnlySpan<byte> data)
    {
        var rop = new RopWriter();
        rop.UInt8(RopWriteStream);
        rop.UInt8(0);
        rop.UInt8(streamHandleIndex);
        rop.UInt16((ushort)data.Length);
        rop.Bytes(data);
        return rop.ToArray();
    }

    public static ushort ParseWriteStream(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopWriteStream, nameof(RopWriteStream));
        return reader.UInt16();          // WrittenSize
    }

    public static byte[] BuildCommitStream(byte streamHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopCommitStream);
        rop.UInt8(0);
        rop.UInt8(streamHandleIndex);
        return rop.ToArray();
    }

    public static void ParseCommitStream(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopCommitStream, nameof(RopCommitStream));

    [Flags]
    public enum SubmitFlags : byte
    {
        None = 0x00,
        PreProcess = 0x01,
        NeedsSpooler = 0x02,
    }

    /// <summary>RopSubmitMessage (MS-OXCROPS 2.2.7.1) on an open, saved message.</summary>
    public static byte[] BuildSubmitMessage(byte messageHandleIndex, SubmitFlags flags = SubmitFlags.None)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSubmitMessage);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt8((byte)flags);
        return rop.ToArray();
    }

    public static void ParseSubmitMessage(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopSubmitMessage, nameof(RopSubmitMessage));

    // ---------------------------------------------------------------------------------------------
    // Recipients
    // ---------------------------------------------------------------------------------------------

    public enum RecipientType : byte
    {
        To = 0x01,
        Cc = 0x02,
        Bcc = 0x03,
    }

    /// <summary>
    /// One recipient row. <paramref name="Flags"/> and <paramref name="TrackStatus"/> are the meeting
    /// columns (PidTagRecipientFlags: 0x1 sendable, 0x2 organizer; PidTagRecipientTrackStatus); when any
    /// row sets them the table carries those two columns for every row.
    /// </summary>
    public sealed record Recipient(RecipientType Type, string DisplayName, string SmtpAddress, uint? Flags = null, uint? TrackStatus = null);

    /// <summary>RopRemoveAllRecipients (MS-OXCROPS 2.2.6.4): empties the recipient table of an open message.</summary>
    public static byte[] BuildRemoveAllRecipients(byte messageHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopRemoveAllRecipients);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt32(0);                                       // Reserved
        return rop.ToArray();
    }

    public static void ParseRemoveAllRecipients(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopRemoveAllRecipients, nameof(RopRemoveAllRecipients));

    /// <summary>
    /// RopModifyRecipients (MS-OXCROPS 2.2.6.5): replaces the recipient table with these rows. Each row is
    /// a ModifyRecipientRow: RowId, RecipientType, then a RecipientRow (MS-OXCDATA 2.8.3.2) whose flags
    /// say which of its optional fields follow; SMTP recipients use address type 3 with the email
    /// address, display name and Unicode strings present, then the column properties as a PropertyRow.
    /// </summary>
    public static byte[] BuildModifyRecipients(byte messageHandleIndex, IReadOnlyList<Recipient> recipients)
    {
        var meeting = recipients.Any(r => r.Flags is not null || r.TrackStatus is not null);
        var columns = meeting ? MeetingRecipientColumns : RecipientColumnsUsed;

        var rop = new RopWriter();
        rop.UInt8(RopModifyRecipients);
        rop.UInt8(0);
        rop.UInt8(messageHandleIndex);
        rop.UInt16((ushort)columns.Length);
        foreach (var tag in columns)
            rop.UInt32(tag);

        rop.UInt16((ushort)recipients.Count);
        for (var i = 0; i < recipients.Count; i++)
        {
            var recipient = recipients[i];
            rop.UInt32((uint)i);                             // RowId
            rop.UInt8((byte)recipient.Type);                 // RecipientType

            var row = EncodeRecipientRow(recipient, columns.Length, meeting);
            rop.UInt16((ushort)row.Length);                  // RecipientRowSize
            rop.Bytes(row);
        }

        return rop.ToArray();
    }

    /// <summary>
    /// The columns each recipient row carries beyond the RecipientRow header: display name, address
    /// type, email address and SMTP address, which is what populates the recipient table the way
    /// Outlook expects.
    /// </summary>
    public static readonly uint[] RecipientColumnsUsed =
    [
        PropertyTags.DisplayName,
        0x3002001F,                         // PidTagAddressType
        0x3003001F,                         // PidTagEmailAddress
        0x39FE001F,                         // PidTagSmtpAddress
    ];

    /// <summary>The columns of a meeting's recipient table: the standard four plus flags and tracking status.</summary>
    public static readonly uint[] MeetingRecipientColumns =
    [
        PropertyTags.DisplayName,
        0x3002001F,                         // PidTagAddressType
        0x3003001F,                         // PidTagEmailAddress
        0x39FE001F,                         // PidTagSmtpAddress
        PropertyTags.RecipientFlags,
        PropertyTags.RecipientTrackStatus,
    ];

    private static byte[] EncodeRecipientRow(Recipient recipient, int columnCount, bool meeting)
    {
        // RecipientFlags (MS-OXCDATA 2.8.3.1), low byte then high byte on the wire:
        //   Type = 3 (SMTP) in bits 0-2, E (0x0008) email address present, D (0x0010) display name present,
        //   U (0x0200) strings are Unicode.
        const ushort flags = 0x0003 | 0x0008 | 0x0010 | 0x0200;

        var row = new RopWriter();
        row.UInt16(flags);
        row.UnicodeZ(recipient.SmtpAddress);                 // EmailAddress
        row.UnicodeZ(recipient.DisplayName);                 // DisplayName

        // RecipientColumnCount + RecipientProperties: a standard PropertyRow (flag 0x00, every value present).
        row.UInt16((ushort)columnCount);
        row.UInt8(0x00);
        row.UnicodeZ(recipient.DisplayName);                 // PidTagDisplayName
        row.UnicodeZ("SMTP");                                // PidTagAddressType
        row.UnicodeZ(recipient.SmtpAddress);                 // PidTagEmailAddress
        row.UnicodeZ(recipient.SmtpAddress);                 // PidTagSmtpAddress
        if (meeting)
        {
            row.UInt32(recipient.Flags ?? 0x1);              // PidTagRecipientFlags: sendable unless told otherwise
            row.UInt32(recipient.TrackStatus ?? 0);          // PidTagRecipientTrackStatus
        }
        return row.ToArray();
    }

    public static void ParseModifyRecipients(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopModifyRecipients, nameof(RopModifyRecipients));

    // ---------------------------------------------------------------------------------------------
    // Send-related property values
    // ---------------------------------------------------------------------------------------------

    /// <summary>PidTagSentMailSvrEID (PtypServerId): where the transport files the sent copy.</summary>
    public const uint SentMailSvrEID = 0x674000FB;

    /// <summary>PidTagDeleteAfterSubmit: remove the submitted message once it is sent.</summary>
    public const uint DeleteAfterSubmit = 0x0E01000B;

    /// <summary>
    /// A PtypServerId value for a folder (MS-OXCDATA 2.11.1.4): Ours = 1, FolderId, MessageId 0,
    /// Instance 0, 21 bytes, written as counted binary like any PtypBinary.
    /// </summary>
    public static byte[] FolderServerId(ulong folderId)
    {
        var writer = new RopWriter();
        writer.UInt8(1);                 // Ours
        writer.UInt64(folderId);
        writer.UInt64(0);                // MessageId
        writer.UInt32(0);                // Instance
        return writer.ToArray();
    }
}
