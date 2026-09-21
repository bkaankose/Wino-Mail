using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// One recipient as returned by RopOpenMessage / RopReadRecipients (MS-OXCDATA 2.8.3.2 RecipientRow
/// inside an OpenRecipientRow): the header fields the flags say are present, then the property row
/// over the columns the server chose. Address resolution prefers the SMTP property, then the header
/// address (an SMTP address for Type 3, a legacy DN for Type 1).
/// </summary>
public sealed record OpenRecipient(
    byte RecipientType,
    ushort Flags,
    string? HeaderAddress,
    string? DisplayName,
    PropertyValue[]? Properties)
{
    public const byte TypeTo = 0x01;
    public const byte TypeCc = 0x02;
    public const byte TypeBcc = 0x03;

    public byte AddressKind => (byte)(Flags & 0x0007);
    public bool IsOptional => (RecipientType & 0x0F) == TypeCc;
    public bool IsResource => (RecipientType & 0x0F) == TypeBcc;

    /// <summary>The cell for a tag, or null when the row has no such column (Array.Find gives a default, zero-tag struct).</summary>
    private PropertyValue? Cell(uint tag)
    {
        if (Properties is null)
        {
            return null;
        }

        var cell = Array.Find(Properties, c => c.Tag == tag);
        return cell.Tag == 0 ? null : cell;
    }

    /// <summary>The SMTP address when the row carries one; else the header address (SMTP for Type 3, a DN for Type 1).</summary>
    public string? SmtpAddress
        => Cell(PropertyTags.SmtpAddress)?.AsString is { Length: > 0 } smtp ? smtp
         : AddressKind == 3 ? HeaderAddress
         : Cell(PropertyTags.EmailAddress)?.AsString is { Length: > 0 } email && email.Contains('@') ? email
         : HeaderAddress;

    public string? Name => Cell(PropertyTags.RecipientDisplayName)?.AsString ?? DisplayName ?? Cell(PropertyTags.DisplayName)?.AsString;

    /// <summary>PidTagRecipientTrackStatus (MS-OXOCAL 2.2.4.10.2): 0 none, 1 organized, 2 tentative, 3 accepted, 4 declined, 5 not responded.</summary>
    public uint TrackStatus => Cell(PropertyTags.RecipientTrackStatus)?.AsUInt32 ?? 0;

    /// <summary>PidTagRecipientFlags bit 0x2: the organizer.</summary>
    public bool IsOrganizer => ((Cell(PropertyTags.RecipientFlags)?.AsUInt32 ?? 0) & 0x2) != 0;
}

public static class RecipientRow
{
    // RecipientFlags as a little-endian 16-bit value (MS-OXCDATA 2.8.3.1; the worked example in
    // MS-OXCMSG RopModifyRecipients pins these): low byte R 0x80, S 0x40, T 0x20, D 0x10, E 0x08,
    // Type 0x07; high byte O 0x8000, N 0x0800, I 0x0400, U 0x0200.
    public const ushort FlagT = 0x0020;
    public const ushort FlagD = 0x0010;
    public const ushort FlagE = 0x0008;
    public const ushort FlagO = 0x8000;
    public const ushort FlagI = 0x0400;
    public const ushort FlagU = 0x0200;

    public const byte AddressX500 = 1;
    public const byte AddressSmtp = 3;
    public const byte AddressPdl1 = 6;
    public const byte AddressPdl2 = 7;

    /// <summary>Parses one RecipientRow (the bytes after RecipientRowSize) against the response's columns.</summary>
    public static OpenRecipient Parse(byte recipientType, ReadOnlyMemory<byte> row, IReadOnlyList<uint> columns)
    {
        var r = new RopReader(row);
        var flags = r.UInt16();
        var kind = (byte)(flags & 0x0007);
        var unicode = (flags & FlagU) != 0;

        string? headerAddress = null;
        switch (kind)
        {
            case AddressX500:
                r.UInt8();                                   // AddressPrefixUsed
                r.UInt8();                                   // DisplayType
                headerAddress = r.AsciiZ();                  // X500DN (String8 always)
                break;
            case AddressPdl1:
            case AddressPdl2:
                r.Bytes(r.UInt16());                         // EntryID
                r.Bytes(r.UInt16());                         // SearchKey
                break;
            case 0 when (flags & FlagO) != 0:
                r.AsciiZ();                                  // AddressType (String8)
                break;
        }

        string? email = null, displayName = null;
        if ((flags & FlagE) != 0) email = unicode ? r.UnicodeZ() : r.AsciiZ();
        if ((flags & FlagD) != 0) displayName = unicode ? r.UnicodeZ() : r.AsciiZ();
        if ((flags & FlagI) != 0) _ = unicode ? r.UnicodeZ() : r.AsciiZ();      // SimpleDisplayName
        if ((flags & FlagT) != 0) _ = unicode ? r.UnicodeZ() : r.AsciiZ();      // TransmittableDisplayName

        headerAddress ??= email;

        var columnCount = r.UInt16();
        PropertyValue[]? properties = null;
        if (columnCount > 0 && columnCount <= columns.Count)
        {
            try
            {
                properties = PropertyRow.Read(r, columns.Take(columnCount).ToList());
            }
            catch (MapiFormatException)
            {
                properties = null;                           // an unhandled type in a column: the header still names the recipient
            }
        }

        return new OpenRecipient(recipientType, flags, headerAddress, displayName, properties);
    }
}
