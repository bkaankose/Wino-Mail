using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>One cell of a PropertyRow: a value, an error, or absent.</summary>
public readonly struct PropertyValue
{
    public uint Tag { get; }
    public object? Value { get; }

    /// <summary>MAPI error code for this cell (PtypErrorCode), or null when the value is present.</summary>
    public uint? Error { get; }

    public bool IsPresent => Error is null && Value is not null;

    private PropertyValue(uint tag, object? value, uint? error)
    {
        Tag = tag;
        Value = value;
        Error = error;
    }

    public static PropertyValue Of(uint tag, object? value) => new(tag, value, null);
    public static PropertyValue Absent(uint tag) => new(tag, null, null);
    public static PropertyValue Failed(uint tag, uint error) => new(tag, null, error);

    public string? AsString => Value as string;
    public ulong? AsUInt64 => Value is ulong v ? v : null;
    public uint? AsUInt32 => Value is uint v ? v : null;
    public bool? AsBoolean => Value is bool v ? v : null;
    public byte[]? AsBinary => Value as byte[];
    public DateTime? AsDateTime => Value is DateTime v ? v : null;

    public override string ToString() => Error is { } e ? $"<error 0x{e:X8}>" : Value?.ToString() ?? "<absent>";
}

/// <summary>
/// PropertyRow (MS-OXCDATA 2.8.1). The leading flag decides the shape of everything after it:
/// 0x00 means every column has a value, 0x01 means each column is preceded by its own flag and
/// may be absent or an error. Reading a flagged row as a standard one silently shifts every
/// subsequent value by a byte.
/// </summary>
public static class PropertyRow
{
    public static PropertyValue[] Read(RopReader reader, IReadOnlyList<uint> columns)
    {
        var flag = reader.UInt8();
        var values = new PropertyValue[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            var tag = columns[i];

            if (flag == 0x00)
            {
                values[i] = PropertyValue.Of(tag, ReadValue(reader, (ushort)(tag & 0xFFFF)));
                continue;
            }

            var valueFlag = reader.UInt8();
            values[i] = valueFlag switch
            {
                0x00 => PropertyValue.Of(tag, ReadValue(reader, (ushort)(tag & 0xFFFF))),
                0x01 => PropertyValue.Absent(tag),
                0x0A => PropertyValue.Failed(tag, reader.UInt32()),           // PtypErrorCode follows
                _ => throw new MapiFormatException($"Unhandled flagged property value flag 0x{valueFlag:X2}."),
            };
        }

        return values;
    }

    /// <summary>Reads a bare property value of the given type (the tag's low 16 bits).</summary>
    public static object ReadValue(RopReader reader, ushort propertyType) => propertyType switch
    {
        PropertyTypes.Short => reader.UInt16(),
        PropertyTypes.Long => reader.UInt32(),
        PropertyTypes.Double => BitConverter.Int64BitsToDouble(unchecked((long)reader.UInt64())),
        PropertyTypes.Boolean => reader.UInt8() != 0,
        PropertyTypes.LongLong => reader.UInt64(),
        PropertyTypes.String8 => reader.AsciiZ(),
        PropertyTypes.Unicode => reader.UnicodeZ(),
        PropertyTypes.SysTime => DateTime.FromFileTimeUtc(unchecked((long)reader.UInt64())),
        PropertyTypes.Guid => new Guid(reader.Bytes(16).Span),
        PropertyTypes.Binary => reader.Bytes(reader.UInt16()).ToArray(),
        PropertyTypes.MultipleBinary => ReadMultipleBinary(reader),
        PropertyTypes.MultipleUnicode => ReadMultipleUnicode(reader),
        PropertyTypes.Restriction => Restrictions.Restriction.Decode(reader, extendedFormat: false),   // rules table rows carry the narrow form
        PropertyTypes.RuleAction => Rules.RuleActions.Read(reader, extendedFormat: false),
        _ => throw new MapiFormatException($"Unhandled property type 0x{propertyType:X4}."),
    };

    /// <summary>
    /// PtypMultipleBinary in a ROP buffer (MS-OXCDATA 2.11.1): a 16-bit count, then that many counted
    /// binaries. (The 32-bit count form belongs to the extended-rule encoding, not to ROPs.)
    /// </summary>
    private static byte[][] ReadMultipleBinary(RopReader reader)
    {
        var count = reader.UInt16();
        var values = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.Bytes(reader.UInt16()).ToArray();
        }

        return values;
    }

    /// <summary>PtypMultipleString in a ROP buffer: a 16-bit count, then that many NUL-terminated strings.</summary>
    private static string[] ReadMultipleUnicode(RopReader reader)
    {
        var count = reader.UInt16();
        var values = new string[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.UnicodeZ();
        }

        return values;
    }
}

/// <summary>Property type ids (MS-OXCDATA 2.11.1), the low word of a property tag.</summary>
public static class PropertyTypes
{
    public const ushort Short = 0x0002;
    public const ushort Long = 0x0003;
    public const ushort Float = 0x0004;
    public const ushort Double = 0x0005;
    public const ushort Currency = 0x0006;
    public const ushort AppTime = 0x0007;
    public const ushort Boolean = 0x000B;
    public const ushort Object = 0x000D;
    public const ushort LongLong = 0x0014;
    public const ushort String8 = 0x001E;
    public const ushort Unicode = 0x001F;
    public const ushort SysTime = 0x0040;
    public const ushort Guid = 0x0048;
    public const ushort Binary = 0x0102;
    public const ushort MultipleBinary = 0x1102;
    public const ushort MultipleUnicode = 0x101F;
    public const ushort MultipleString8 = 0x101E;
    public const ushort MultipleGuid = 0x1048;
    public const ushort ErrorCode = 0x000A;
    public const ushort ServerId = 0x00FB;
    public const ushort Restriction = 0x00FD;
    public const ushort RuleAction = 0x00FE;
}

/// <summary>Property tags used so far, by name.</summary>
public static class PropertyTags
{
    public const uint MessageClass = 0x001A001F;                 // PidTagMessageClass
    /// <summary>
    /// PidTagRtfInSync: the plain or HTML body is the message's real body, and no rich text body is
    /// waiting to be reconciled with it. Left unset, the transport assumes there is one it cannot see
    /// and encodes the message to preserve it, which costs a meeting request its class on the way out.
    /// </summary>
    public const uint RtfInSync = 0x0E1F000B;
    public const uint Subject = 0x0037001F;                      // PidTagSubject
    public const uint DisplayName = 0x3001001F;                  // PidTagDisplayName
    public const uint RuleMessageProvider = 0x65EB001F;          // PidTagRuleMessageProvider
    public const uint RuleMessageName = 0x65EC001F;              // PidTagRuleMessageName
    public const uint Mid = 0x674A0014;                          // PidTagMid
    public const uint FolderId = 0x67480014;                     // PidTagFolderId
    public const uint ParentFolderId = 0x67490014;               // PidTagParentFolderId
    public const uint EntryId = 0x0FFF0102;                      // PidTagEntryId
    public const uint ContainerClass = 0x3613001F;               // PidTagContainerClass ("IPF.Note", ...)
    public const uint ContentCount = 0x36020003;                 // PidTagContentCount
    public const uint ContentUnreadCount = 0x36030003;           // PidTagContentUnreadCount
    public const uint Subfolders = 0x360A000B;                   // PidTagSubfolders
    public const uint IpmDraftsEntryId = 0x36D70102;             // PidTagIpmDraftsEntryId (on the Inbox / Root)
    public const uint AdditionalRenEntryIds = 0x36D81102;        // PidTagAdditionalRenEntryIds (Conflicts, Sync Issues, Local Failures, Server Failures, Junk)
    public const uint ExtendedRuleMessageCondition = 0x0E9A0102; // PidTagExtendedRuleMessageCondition
    public const uint ExtendedRuleMessageActions = 0x0E990102;   // PidTagExtendedRuleMessageActions

    // --- Message list / read (MS-OXOMSG, MS-OXCMSG, MS-OXPROPS) ---
    public const uint Importance = 0x00170003;                   // PidTagImportance (0 low, 1 normal, 2 high)
    public const uint SentRepresentingName = 0x0042001F;         // PidTagSentRepresentingName
    public const uint SentRepresentingSmtpAddress = 0x5D02001F;  // PidTagSentRepresentingSmtpAddress
    public const uint SenderName = 0x0C1A001F;                   // PidTagSenderName
    public const uint SenderSmtpAddress = 0x5D01001F;            // PidTagSenderSmtpAddress
    public const uint ClientSubmitTime = 0x00390040;             // PidTagClientSubmitTime (sent)
    public const uint MessageDeliveryTime = 0x0E060040;          // PidTagMessageDeliveryTime (received)
    public const uint LastModificationTime = 0x30080040;         // PidTagLastModificationTime
    public const uint MessageFlags = 0x0E070003;                 // PidTagMessageFlags (MSGFLAG_READ 0x01, HASATTACH 0x10, UNSENT 0x08)
    public const uint MessageSize = 0x0E080003;                  // PidTagMessageSize
    public const uint HasAttachments = 0x0E1B000B;               // PidTagHasAttachments
    public const uint NormalizedSubject = 0x0E1D001F;            // PidTagNormalizedSubject
    public const uint DisplayTo = 0x0E04001F;                    // PidTagDisplayTo
    public const uint DisplayCc = 0x0E03001F;                    // PidTagDisplayCc
    public const uint ConversationId = 0x30130102;               // PidTagConversationId
    public const uint ConversationIndex = 0x00710102;            // PidTagConversationIndex
    public const uint InternetMessageId = 0x1035001F;            // PidTagInternetMessageId
    public const uint InReplyToId = 0x1042001F;                  // PidTagInReplyToId
    public const uint InternetReferences = 0x1039001F;           // PidTagInternetReferences
    public const uint FlagStatus = 0x10900003;                   // PidTagFlagStatus (1 complete, 2 flagged)
    public const uint ChangeKey = 0x65E20102;                    // PidTagChangeKey
    public const uint Body = 0x1000001F;                         // PidTagBody (plain text)
    public const uint Html = 0x10130102;                         // PidTagHtml (bytes in PidTagInternetCodepage)
    public const uint RtfCompressed = 0x10090102;                // PidTagRtfCompressed
    public const uint InternetCodepage = 0x3FDE0003;             // PidTagInternetCodepage
    public const uint TransportMessageHeaders = 0x007D001F;      // PidTagTransportMessageHeaders
    public const uint ReadReceiptRequested = 0x0029000B;         // PidTagReadReceiptRequested

    // --- Attachments (MS-OXCMSG 2.2.2) ---
    public const uint AttachNumber = 0x0E210003;                 // PidTagAttachNumber
    public const uint AttachMethod = 0x37050003;                 // PidTagAttachMethod (1 by value, 5 embedded message, 6 OLE)
    public const uint AttachSize = 0x0E200003;                   // PidTagAttachSize
    public const uint AttachLongFilename = 0x3707001F;           // PidTagAttachLongFilename
    public const uint AttachFilename = 0x3704001F;               // PidTagAttachFilename
    public const uint AttachMimeTag = 0x370E001F;                // PidTagAttachMimeTag
    public const uint AttachContentId = 0x3712001F;              // PidTagAttachContentId
    public const uint AttachmentHidden = 0x7FFE000B;             // PidTagAttachmentHidden
    public const uint AttachDataBinary = 0x37010102;             // PidTagAttachDataBinary (stream it)
    public const uint DisplayNameAttach = DisplayName;

    /// <summary>MSGFLAG values inside PidTagMessageFlags.</summary>
    public const uint MessageFlagRead = 0x00000001;
    public const uint MessageFlagUnsent = 0x00000008;
    public const uint MessageFlagHasAttach = 0x00000010;

    /// <summary>The columns a message list read asks for (one row per message, all small).</summary>
    public static readonly uint[] MessageListColumns =
    [
        Mid, MessageClass, Subject, NormalizedSubject, SentRepresentingName, SentRepresentingSmtpAddress,
        SenderName, SenderSmtpAddress, ClientSubmitTime, MessageDeliveryTime, LastModificationTime,
        MessageFlags, MessageSize, HasAttachments, Importance, FlagStatus, ConversationId,
        InternetMessageId, InReplyToId, InternetReferences, DisplayTo, ChangeKey,
    ];

    /// <summary>The columns an attachment table read asks for.</summary>
    public static readonly uint[] AttachmentColumns =
        [AttachNumber, AttachMethod, AttachSize, AttachLongFilename, AttachFilename, AttachMimeTag, AttachContentId, AttachmentHidden, DisplayName];

    /// <summary>The columns a rule/junk FAI table read asks for.</summary>
    public static readonly uint[] RuleColumns = [MessageClass, RuleMessageProvider, RuleMessageName, Mid];

    // --- Rules table (MS-OXORULE 2.2.1.3) ---
    public const uint RuleId = 0x66740014;                       // PidTagRuleId (0x6674; 0x6774 answers MAPI_E_NOT_FOUND per row, found the hard way)
    public const uint RuleSequence = 0x66760003;                 // PidTagRuleSequence
    public const uint RuleState = 0x66770003;                    // PidTagRuleState (ST_ENABLED 0x01, ST_ERROR 0x02, ST_ONLY_WHEN_OOF 0x04, ST_EXIT_LEVEL 0x10, ...)
    public const uint RuleName = 0x6682001F;                     // PidTagRuleName
    public const uint RuleProvider = 0x6681001F;                 // PidTagRuleProvider
    public const uint RuleLevel = 0x66830003;                    // PidTagRuleLevel
    public const uint RuleUserFlags = 0x66780003;                // PidTagRuleUserFlags
    public const uint RuleProviderData = 0x66840102;             // PidTagRuleProviderData
    public const uint RuleCondition = 0x667900FD;                // PidTagRuleCondition (PtypRestriction)
    public const uint RuleActions = 0x668000FE;                  // PidTagRuleActions (PtypRuleAction)

    // --- Rule conditions and recipients (MS-OXORULE, MS-OXOABK) ---
    public const uint SenderSearchKey = 0x0C1D0102;              // PidTagSenderSearchKey ("SMTP:ADDR" / "EX:/O=...")
    public const uint SearchKey = 0x300B0102;                    // PidTagSearchKey (recipient rows)
    public const uint SenderEmailAddress = 0x0C1F001F;           // PidTagSenderEmailAddress
    public const uint MessageRecipients = 0x0E12000D;            // PidTagMessageRecipients (subobject for recipient restrictions)
    public const uint EmailAddress = 0x3003001F;                 // PidTagEmailAddress
    public const uint AddressType = 0x3002001F;                  // PidTagAddressType ("SMTP", "EX")
    public const uint SmtpAddress = 0x39FE001F;                  // PidTagSmtpAddress
    public const uint RecipientType = 0x0C150003;                // PidTagRecipientType (1 To, 2 Cc, 3 Bcc)
    public const uint RwRulesStream = 0x68020102;                // PidTagRwRulesStream (the classic Outlook rule blob)
    public const uint RecipientDisplayName = 0x5FF6001F;         // PidTagRecipientDisplayName
    public const uint RecipientFlags = 0x5FFD0003;               // PidTagRecipientFlags (0x1 sendable, 0x2 organizer)
    public const uint RecipientTrackStatus = 0x5FFF0003;         // PidTagRecipientTrackStatus (0 none, 1 organized, 2 tentative, 3 accepted, 4 declined, 5 not responded)
    public const uint RuleCommentType = 0x60000003;              // Outlook's RES_COMMENT marker inside rule conditions (1 = recipient list)

    public const string RuleMessageClass = "IPM.Rule.Version2.Message";
    public const string ExtendedRuleMessageClass = "IPM.ExtendedRule.Message";
    public const string RuleOrganizerMessageClass = "IPM.RuleOrganizer";
    public const string JunkEmailRuleProvider = "JunkEmailRule";
    public const string RuleOrganizerProvider = "RuleOrganizer";

    // --- Contacts (MS-OXOCNTC) ---
    public const uint GivenName = 0x3A06001F;                    // PidTagGivenName
    public const uint Surname = 0x3A11001F;                      // PidTagSurname
    public const uint MiddleName = 0x3A44001F;                   // PidTagMiddleName
    public const uint Nickname = 0x3A4F001F;                     // PidTagNickname
    public const uint CompanyName = 0x3A16001F;                  // PidTagCompanyName
    public const uint JobTitle = 0x3A17001F;                     // PidTagTitle
    public const uint DepartmentName = 0x3A18001F;               // PidTagDepartmentName
    public const uint BusinessTelephoneNumber = 0x3A08001F;      // PidTagBusinessTelephoneNumber
    public const uint HomeTelephoneNumber = 0x3A09001F;          // PidTagHomeTelephoneNumber
    public const uint MobileTelephoneNumber = 0x3A1C001F;        // PidTagMobileTelephoneNumber
    public const uint Birthday = 0x3A420040;                     // PidTagBirthday
    public const uint BusinessHomePage = 0x3A51001F;             // PidTagBusinessHomePage
    public const uint HomeAddressStreet = 0x3A5D001F;            // PidTagHomeAddressStreet
    public const uint HomeAddressCity = 0x3A59001F;              // PidTagHomeAddressCity
    public const uint HomeAddressStateOrProvince = 0x3A5C001F;   // PidTagHomeAddressStateOrProvince
    public const uint HomeAddressPostalCode = 0x3A5B001F;        // PidTagHomeAddressPostalCode
    public const uint HomeAddressCountry = 0x3A5A001F;           // PidTagHomeAddressCountry
    public const uint AttachmentContactPhoto = 0x7FFF000B;       // PidTagAttachmentContactPhoto (on the attachment)
    public const uint AttachmentFlags = 0x7FFD0003;              // PidTagAttachmentFlags (0x2 afException)
    public const uint AttachmentLinkId = 0x7FFA0003;             // PidTagAttachmentLinkId
    public const uint RenderingPosition = 0x370B0003;            // PidTagRenderingPosition (0xFFFFFFFF hidden)
    public const uint ExceptionStartTime = 0x7FFB0040;           // PidTagExceptionStartTime (wall clock, on the exception attachment)
    public const uint ExceptionEndTime = 0x7FFC0040;             // PidTagExceptionEndTime
    public const uint ExceptionReplaceTime = 0x7FF90040;         // PidTagExceptionReplaceTime (UTC original start)
    public const string ExceptionMessageClass = "IPM.OLE.CLASS.{00061055-0000-0000-C000-000000000046}";

    /// <summary>PSETID_Address: the property set of the contact named properties (email addresses, work address).</summary>
    public static readonly Guid AddressPropertySet = new("00062004-0000-0000-C000-000000000046");
    public const uint LidEmail1EmailAddress = 0x8083;            // PidLidEmail1EmailAddress
    public const uint LidEmail2EmailAddress = 0x8093;            // PidLidEmail2EmailAddress
    public const uint LidEmail3EmailAddress = 0x80A3;            // PidLidEmail3EmailAddress
    public const uint LidWorkAddressStreet = 0x8045;             // PidLidWorkAddressStreet
    public const uint LidWorkAddressCity = 0x8046;               // PidLidWorkAddressCity
    public const uint LidWorkAddressState = 0x8047;              // PidLidWorkAddressState
    public const uint LidWorkAddressPostalCode = 0x8048;         // PidLidWorkAddressPostalCode
    public const uint LidWorkAddressCountry = 0x8049;            // PidLidWorkAddressCountry
    public const uint LidFileUnder = 0x8005;                     // PidLidFileUnder ("File as")
    public const uint LidEmail1DisplayName = 0x8080;             // PidLidEmail1DisplayName
    public const uint LidEmail1AddressType = 0x8082;             // PidLidEmail1AddressType ("SMTP")
    public const uint LidEmail1OriginalDisplayName = 0x8084;     // PidLidEmail1OriginalDisplayName
    public const uint LidEmail2AddressType = 0x8092;             // PidLidEmail2AddressType
    public const uint LidEmail2OriginalDisplayName = 0x8094;     // PidLidEmail2OriginalDisplayName
    public const uint LidEmail3AddressType = 0x80A2;             // PidLidEmail3AddressType
    public const uint LidEmail3OriginalDisplayName = 0x80A4;     // PidLidEmail3OriginalDisplayName
    public const uint BusinessFaxNumber = 0x3A24001F;            // PidTagBusinessFaxNumber
    public const uint IpmContactEntryId = 0x36D10102;            // PidTagIpmContactEntryId (on the Inbox / Root)
    public const uint IpmTaskEntryId = 0x36D40102;               // PidTagIpmTaskEntryId
    public const uint IpmAppointmentEntryId = 0x36D00102;        // PidTagIpmAppointmentEntryId

    // --- Tasks (MS-OXOTASK) ---
    public static readonly Guid TaskPropertySet = new("00062003-0000-0000-C000-000000000046");     // PSETID_Task
    public static readonly Guid CommonPropertySet = new("00062008-0000-0000-C000-000000000046");   // PSETID_Common
    public const uint LidTaskStatus = 0x8101;                    // PidLidTaskStatus (0 not started, 1 in progress, 2 complete, 3 waiting, 4 deferred)
    public const uint LidPercentComplete = 0x8102;               // PidLidPercentComplete (PtypFloating64, 0.0-1.0)
    public const uint LidTaskStartDate = 0x8104;                 // PidLidTaskStartDate
    public const uint LidTaskDueDate = 0x8105;                   // PidLidTaskDueDate
    public const uint LidTaskDateCompleted = 0x810F;             // PidLidTaskDateCompleted
    public const uint LidTaskComplete = 0x811C;                  // PidLidTaskComplete
    public const uint LidTaskOwner = 0x811F;                     // PidLidTaskOwner
    public const uint LidReminderSet = 0x8503;                   // PidLidReminderSet (PSETID_Common)
    public const uint LidReminderTime = 0x8502;                  // PidLidReminderTime (PSETID_Common)
    public const uint LidReminderDelta = 0x8501;                 // PidLidReminderDelta (minutes before start, PSETID_Common)
    public const string ClientTrackingIdPropertyName = "WinoClientTrackingId"; // PS_PUBLIC_STRINGS string-named, written on create
    public const uint LidCommonStart = 0x8516;                   // PidLidCommonStart (PSETID_Common)
    public const uint LidCommonEnd = 0x8517;                     // PidLidCommonEnd (PSETID_Common)
    public const string TaskMessageClass = "IPM.Task";

    // --- Calendar (MS-OXOCAL) ---
    public static readonly Guid AppointmentPropertySet = new("00062002-0000-0000-C000-000000000046");  // PSETID_Appointment
    public static readonly Guid MeetingPropertySet = new("6ED8DA90-450B-101B-98DA-00AA003F1305");      // PSETID_Meeting
    public const uint LidAppointmentStartWhole = 0x820D;         // PidLidAppointmentStartWhole (UTC)
    public const uint LidAppointmentEndWhole = 0x820E;           // PidLidAppointmentEndWhole (UTC)
    public const uint LidLocation = 0x8208;                      // PidLidLocation
    public const uint LidAppointmentSubType = 0x8215;            // PidLidAppointmentSubType (all-day)
    public const uint LidBusyStatus = 0x8205;                    // PidLidBusyStatus (0 free, 1 tentative, 2 busy, 3 OOF, 4 working elsewhere)
    public const uint LidRecurring = 0x8223;                     // PidLidRecurring
    public const uint LidAppointmentRecur = 0x8216;              // PidLidAppointmentRecur (AppointmentRecurrencePattern blob)
    public const uint LidResponseStatus = 0x8218;                // PidLidResponseStatus
    public const uint LidAppointmentStateFlags = 0x8217;         // PidLidAppointmentStateFlags (1 meeting, 2 received, 4 cancelled)
    public const uint LidTimeZoneDescription = 0x8234;           // PidLidTimeZoneDescription
    public const uint LidRecurrenceType = 0x8231;                // PidLidRecurrenceType (1 daily, 2 weekly, 3 monthly, 4 yearly)
    public const uint LidClipStart = 0x8235;                     // PidLidClipStart (UTC midnight of the first occurrence)
    public const uint LidClipEnd = 0x8236;                       // PidLidClipEnd (UTC midnight of the last; 4500-08-31 for no end)
    public const uint LidTimeZoneStruct = 0x8233;                // PidLidTimeZoneStruct
    public const uint LidAppointmentTimeZoneDefinitionStartDisplay = 0x825E; // PidLidAppointmentTimeZoneDefinitionStartDisplay
    public const uint LidAppointmentTimeZoneDefinitionEndDisplay = 0x825F;   // PidLidAppointmentTimeZoneDefinitionEndDisplay
    public const uint LidAppointmentTimeZoneDefinitionRecur = 0x8260;        // PidLidAppointmentTimeZoneDefinitionRecur
    public const uint LidGlobalObjectId = 0x0003;                // PidLidGlobalObjectId (PSETID_Meeting)
    public const uint LidCleanGlobalObjectId = 0x0023;           // PidLidCleanGlobalObjectId (PSETID_Meeting)
    public const uint LidExceptionReplaceTime = 0x8228;          // PidLidExceptionReplaceTime
    public const uint LidAppointmentSequence = 0x8201;           // PidLidAppointmentSequence
    public const uint LidAppointmentReplyTime = 0x8220;          // PidLidAppointmentReplyTime
    public const uint LidIntendedBusyStatus = 0x8224;            // PidLidIntendedBusyStatus
    public const uint LidAppointmentReplyName = 0x8230;          // PidLidAppointmentReplyName
    public const uint LidAttendeeCriticalChange = 0x0001;        // PidLidAttendeeCriticalChange (PSETID_Meeting)
    public const uint LidWhere = 0x0002;                         // PidLidWhere (PSETID_Meeting)
    public const uint LidOwnerCriticalChange = 0x001A;           // PidLidOwnerCriticalChange (PSETID_Meeting)
    public const uint LidIsRecurring = 0x0005;                   // PidLidIsRecurring (PSETID_Meeting)
    public const uint LidIsException = 0x000A;                   // PidLidIsException (PSETID_Meeting)
    public const uint LidFInvited = 0x8229;                      // PidLidFInvited (organizer has sent the request)
    public const uint LidAppointmentDuration = 0x8213;           // PidLidAppointmentDuration (minutes)
    public const uint ResponseRequested = 0x0063000B;            // PidTagResponseRequested
    public const string MeetingRequestClass = "IPM.Schedule.Meeting.Request";
    public const string MeetingCanceledClass = "IPM.Schedule.Meeting.Canceled";
    public const string MeetingResponseAcceptClass = "IPM.Schedule.Meeting.Resp.Pos";
    public const string MeetingResponseTentativeClass = "IPM.Schedule.Meeting.Resp.Tent";
    public const string MeetingResponseDeclineClass = "IPM.Schedule.Meeting.Resp.Neg";
    public const uint Sensitivity = 0x00360003;                  // PidTagSensitivity (0 normal, 1 personal, 2 private, 3 confidential)
    public const uint CreationTime = 0x30070040;                 // PidTagCreationTime
    public const uint SentRepresentingEmailAddress = 0x0065001F; // PidTagSentRepresentingEmailAddress
    public const string AppointmentMessageClass = "IPM.Appointment";
    public const string CalendarContainerClass = "IPF.Appointment";

    public const string ContactMessageClass = "IPM.Contact";
    public const string ContactContainerClass = "IPF.Contact";

    /// <summary>PS_PUBLIC_STRINGS, the property set of the Keywords (categories) named property.</summary>
    public static readonly Guid PublicStringsPropertySet = new("00020329-0000-0000-C000-000000000046");
    public const string KeywordsPropertyName = "Keywords";

    /// <summary>The columns a rules-table read asks for.</summary>
    public static readonly uint[] RulesTableColumns =
        [RuleId, RuleSequence, RuleState, RuleName, RuleProvider, RuleLevel, RuleUserFlags, RuleProviderData, RuleCondition, RuleActions];

    /// <summary>
    /// The columns a folder hierarchy read asks for. PidTagEntryId is deliberately not among them:
    /// Exchange answers it with MAPI_E_NOT_FOUND in a hierarchy table (found the hard way: 27 rows,
    /// every one dropped). Entry ids are built from RopLongTermIdFromId instead.
    /// </summary>
    public static readonly uint[] HierarchyColumns =
        [FolderId, ParentFolderId, DisplayName, ContainerClass, Subfolders, ContentCount, ContentUnreadCount];

    public const uint AttributeHidden = 0x10F4000B;              // PidTagAttributeHidden (system folders OWA/Outlook keep out of the tree)

    /// <summary>The hierarchy columns plus the hidden flag; ToFolderInfo takes either width.</summary>
    public static readonly uint[] HierarchyColumnsWithHidden =
        [FolderId, ParentFolderId, DisplayName, ContainerClass, Subfolders, ContentCount, ContentUnreadCount, AttributeHidden];

    /// <summary>MS-OXOSFLD 2.2.4: the index of the Junk E-mail entry id inside PidTagAdditionalRenEntryIds.</summary>
    public const int AdditionalRenEntryIdsJunkIndex = 4;

    public static string Describe(uint tag) => tag switch
    {
        MessageClass => "MessageClass",
        Subject => "Subject",
        DisplayName => "DisplayName",
        RuleMessageProvider => "RuleProvider",
        RuleMessageName => "RuleName",
        Mid => "Mid",
        FolderId => "FolderId",
        ParentFolderId => "ParentFolderId",
        EntryId => "EntryId",
        ContainerClass => "ContainerClass",
        ContentCount => "ContentCount",
        ContentUnreadCount => "ContentUnreadCount",
        Subfolders => "Subfolders",
        IpmDraftsEntryId => "IpmDraftsEntryId",
        AdditionalRenEntryIds => "AdditionalRenEntryIds",
        ExtendedRuleMessageCondition => "ExtendedRuleMessageCondition",
        ExtendedRuleMessageActions => "ExtendedRuleMessageActions",
        _ => $"0x{tag:X8}",
    };
}
