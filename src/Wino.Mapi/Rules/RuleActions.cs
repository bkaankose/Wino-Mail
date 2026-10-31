using System.Buffers.Binary;
using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Rules;

/// <summary>Action types (MS-OXORULE 2.2.5.1.1 ActionType).</summary>
public enum RuleActionType : byte
{
    Move = 0x01,
    Copy = 0x02,
    Reply = 0x03,
    OofReply = 0x04,
    DeferAction = 0x05,
    Bounce = 0x06,
    Forward = 0x07,
    Delegate = 0x08,
    Tag = 0x09,
    Delete = 0x0A,
    MarkAsRead = 0x0B,
}

/// <summary>A recipient of a Forward or Delegate action, reduced to what the client shows.</summary>
public sealed record RuleRecipient(string DisplayName, string Address);

/// <summary>One action of a rule, with the part of its data this client understands decoded.</summary>
public sealed class RuleAction
{
    public RuleActionType Type { get; init; }
    public uint Flavor { get; init; }
    public uint Flags { get; init; }

    /// <summary>Move/Copy: the destination folder's EntryID (the FolderEID inside the action data).</summary>
    public byte[]? FolderEntryId { get; init; }

    /// <summary>Move/Copy: the store EntryID; empty when the folder is in this store.</summary>
    public byte[]? StoreEntryId { get; init; }

    /// <summary>
    /// Move/Copy into this store: the folder id, when the FolderEID is a ServerEid (MS-OXORULE
    /// 2.2.5.1.2.1.1: Ours=1, FolderId, MessageId, Instance). Null when it is a real EntryID.
    /// </summary>
    public ulong? ServerFolderId =>
        FolderEntryId is { Length: 21 } eid && eid[0] == 1 ? BinaryPrimitives.ReadUInt64LittleEndian(eid.AsSpan(1, 8)) : null;

    /// <summary>Forward/Delegate: the recipients, when their blocks decoded; null when kept raw.</summary>
    public List<RuleRecipient>? Recipients { get; init; }

    /// <summary>Anything not decoded (Reply/OofReply template ids, DeferAction data, undecodable recipient blocks).</summary>
    public byte[]? RawData { get; init; }

    /// <summary>Tag: the property written (a TaggedPropertyValue).</summary>
    public uint? TagPropertyTag { get; init; }
    public object? TagValue { get; init; }

    /// <summary>Bounce: the bounce code.</summary>
    public uint? BounceCode { get; init; }

    // ---- Builders (the actions this client writes) --------------------------------------------

    public static RuleAction MoveTo(ulong folderId) => new() { Type = RuleActionType.Move, FolderEntryId = ServerEid(folderId), StoreEntryId = [] };
    public static RuleAction CopyTo(ulong folderId) => new() { Type = RuleActionType.Copy, FolderEntryId = ServerEid(folderId), StoreEntryId = [] };
    public static RuleAction Delete() => new() { Type = RuleActionType.Delete };
    public static RuleAction MarkAsRead() => new() { Type = RuleActionType.MarkAsRead };
    public static RuleAction ForwardTo(IReadOnlyList<RuleRecipient> recipients) => new() { Type = RuleActionType.Forward, Recipients = [.. recipients] };
    public static RuleAction TagWith(uint propertyTag, object value) => new() { Type = RuleActionType.Tag, TagPropertyTag = propertyTag, TagValue = value };

    private static byte[] ServerEid(ulong folderId)
    {
        var eid = new byte[21];
        eid[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(eid.AsSpan(1), folderId);
        return eid;
    }
}

/// <summary>
/// RuleActions (MS-OXORULE 2.2.5): NoOfActions, then ActionBlocks each with ActionLength, ActionType,
/// ActionFlavor, ActionFlags and type-specific data. In a rules table row (PtypRuleAction, type 0x00FE)
/// the counts are 16-bit; the extended-rule form (PidTagExtendedRuleMessageActions) uses 32-bit counts
/// and lengths, and additionally starts with a NamedPropertyInformation block.
/// </summary>
public static class RuleActions
{
    public const uint RecipientTypeTo = 1;

    public static List<RuleAction> Read(RopReader reader, bool extendedFormat = false)
    {
        var count = extendedFormat ? reader.UInt32() : reader.UInt16();
        if (count > 10_000)
            throw new MapiFormatException("Implausible rule action count; the row is misread.");

        var actions = new List<RuleAction>((int)count);
        for (var i = 0; i < count; i++)
        {
            var length = extendedFormat ? reader.UInt32() : reader.UInt16();
            var start = reader.Position;
            var type = (RuleActionType)reader.UInt8();
            var flavor = reader.UInt32();
            var flags = reader.UInt32();
            var dataLength = (int)length - (reader.Position - start);
            if (dataLength < 0)
                throw new MapiFormatException("Rule action length is smaller than its header.");

            var raw = reader.Bytes(dataLength).ToArray();
            try
            {
                actions.Add(ReadAction(type, flavor, flags, new RopReader(raw), extendedFormat));
            }
            catch (MapiFormatException)
            {
                // The block is length-delimited, so a data layout this client does not understand is
                // kept raw rather than failing the whole table; the caller treats it as unsupported.
                actions.Add(new RuleAction { Type = type, Flavor = flavor, Flags = flags, RawData = raw });
            }
        }

        return actions;
    }

    private static RuleAction ReadAction(RuleActionType type, uint flavor, uint flags, RopReader data, bool extendedFormat)
    {
        switch (type)
        {
            case RuleActionType.Move:
            case RuleActionType.Copy:
            {
                // MoveCopyActionData (2.2.5.1.2.1): FolderInThisStore (1), StoreEIDSize + StoreEID, FolderEIDSize + FolderEID.
                // The extended form has no FolderInThisStore byte and uses 32-bit sizes.
                if (!extendedFormat)
                    data.UInt8();
                var storeSize = extendedFormat ? (int)data.UInt32() : data.UInt16();
                var store = data.Bytes(storeSize).ToArray();
                var folderSize = extendedFormat ? (int)data.UInt32() : data.UInt16();
                var folder = data.Bytes(folderSize).ToArray();
                return new RuleAction { Type = type, Flavor = flavor, Flags = flags, StoreEntryId = store, FolderEntryId = folder };
            }

            case RuleActionType.Forward:
            case RuleActionType.Delegate:
            {
                // ForwardDelegateActionData (2.2.5.1.2.4): RecipientCount, then RecipientBlocks of
                // Reserved (1), NoOfProperties (4), TaggedPropertyValues.
                var recipientCount = extendedFormat ? data.UInt32() : data.UInt16();
                var recipients = new List<RuleRecipient>((int)recipientCount);
                for (var i = 0; i < recipientCount; i++)
                {
                    data.UInt8();
                    var propertyCount = data.UInt32();
                    string? displayName = null, email = null, smtp = null;
                    for (var p = 0; p < propertyCount; p++)
                    {
                        var tag = data.UInt32();
                        var value = PropertyRow.ReadValue(data, (ushort)(tag & 0xFFFF));
                        switch (tag)
                        {
                            case PropertyTags.DisplayName: displayName = value as string; break;
                            case PropertyTags.EmailAddress: email = value as string; break;
                            case PropertyTags.SmtpAddress: smtp = value as string; break;
                        }
                    }

                    recipients.Add(new RuleRecipient(displayName ?? string.Empty, smtp ?? email ?? string.Empty));
                }

                return new RuleAction { Type = type, Flavor = flavor, Flags = flags, Recipients = recipients };
            }

            case RuleActionType.Tag:
            {
                var tag = data.UInt32();
                var value = PropertyRow.ReadValue(data, (ushort)(tag & 0xFFFF));
                return new RuleAction { Type = type, Flavor = flavor, Flags = flags, TagPropertyTag = tag, TagValue = value };
            }

            case RuleActionType.Bounce:
                return new RuleAction { Type = type, Flavor = flavor, Flags = flags, BounceCode = data.UInt32() };

            case RuleActionType.Delete:
            case RuleActionType.MarkAsRead:
                return new RuleAction { Type = type, Flavor = flavor, Flags = flags };

            default:
                // Reply / OofReply (ReplyTemplateEID), DeferAction: kept raw.
                return new RuleAction { Type = type, Flavor = flavor, Flags = flags, RawData = data.Bytes(data.Remaining).ToArray() };
        }
    }

    /// <summary>The narrow (rules table) encoding of the actions this client builds.</summary>
    public static void Write(RopWriter writer, IReadOnlyList<RuleAction> actions)
    {
        writer.UInt16((ushort)actions.Count);
        foreach (var action in actions)
        {
            var block = new RopWriter();
            block.UInt8((byte)action.Type);
            block.UInt32(action.Flavor);
            block.UInt32(action.Flags);
            WriteActionData(block, action);

            var bytes = block.ToArray();
            writer.UInt16((ushort)bytes.Length);
            writer.Bytes(bytes);
        }
    }

    private static void WriteActionData(RopWriter block, RuleAction action)
    {
        switch (action.Type)
        {
            case RuleActionType.Move:
            case RuleActionType.Copy:
            {
                var store = action.StoreEntryId ?? [];
                var folder = action.FolderEntryId ?? throw new MapiFormatException("A move/copy action needs a folder.");
                block.UInt8(store.Length == 0 ? (byte)1 : (byte)0);     // FolderInThisStore
                block.UInt16((ushort)store.Length);
                block.Bytes(store);
                block.UInt16((ushort)folder.Length);
                block.Bytes(folder);
                break;
            }

            case RuleActionType.Forward:
            case RuleActionType.Delegate:
            {
                var recipients = action.Recipients ?? throw new MapiFormatException("A forward action needs recipients.");
                block.UInt16((ushort)recipients.Count);
                foreach (var recipient in recipients)
                {
                    var properties = RecipientProperties(recipient);
                    block.UInt8(0);
                    block.UInt32((uint)properties.Count);
                    foreach (var property in properties)
                    {
                        property.WriteTo(block);
                    }
                }
                break;
            }

            case RuleActionType.Tag:
                new TaggedPropertyValue(action.TagPropertyTag ?? throw new MapiFormatException("A tag action needs a property."), action.TagValue!).WriteTo(block);
                break;

            case RuleActionType.Bounce:
                block.UInt32(action.BounceCode ?? 0);
                break;

            case RuleActionType.Delete:
            case RuleActionType.MarkAsRead:
                break;

            default:
                if (action.RawData is null)
                    throw new MapiFormatException($"Cannot encode a {action.Type} action.");
                block.Bytes(action.RawData);
                break;
        }
    }

    /// <summary>
    /// The recipient block for an SMTP address (MS-OXORULE 2.2.5.1.2.4.1): a one-off EntryID plus the
    /// display and address properties Outlook expects to find beside it.
    /// </summary>
    private static List<TaggedPropertyValue> RecipientProperties(RuleRecipient recipient)
    {
        var displayName = string.IsNullOrWhiteSpace(recipient.DisplayName) ? recipient.Address : recipient.DisplayName;
        return
        [
            TaggedPropertyValue.Binary(PropertyTags.EntryId, OneOffEntryId(displayName, recipient.Address)),
            TaggedPropertyValue.Unicode(PropertyTags.DisplayName, displayName),
            TaggedPropertyValue.Unicode(PropertyTags.AddressType, "SMTP"),
            TaggedPropertyValue.Unicode(PropertyTags.EmailAddress, recipient.Address),
            TaggedPropertyValue.Unicode(PropertyTags.SmtpAddress, recipient.Address),
            TaggedPropertyValue.Long(PropertyTags.RecipientType, RecipientTypeTo),
            TaggedPropertyValue.Binary(PropertyTags.SearchKey, SmtpSearchKey(recipient.Address)),
        ];
    }

    /// <summary>The search key form of an SMTP address: "SMTP:" + upper-cased address, NUL-terminated ASCII.</summary>
    public static byte[] SmtpSearchKey(string address)
        => Encoding.ASCII.GetBytes("SMTP:" + address.Trim().ToUpperInvariant() + "\0");

    /// <summary>One-off EntryID (MS-OXCDATA 2.2.5.1) for an SMTP recipient: Unicode, no rich info.</summary>
    public static byte[] OneOffEntryId(string displayName, string address)
    {
        var writer = new RopWriter();
        writer.UInt32(0);                                                   // Flags
        writer.Bytes([0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02]);   // ProviderUID: one-off
        writer.UInt16(0);                                                   // Version
        writer.UInt16(0x8001);                                              // U (Unicode) + NoRichInfo
        writer.UnicodeZ(displayName);
        writer.UnicodeZ("SMTP");
        writer.UnicodeZ(address);
        return writer.ToArray();
    }
}
