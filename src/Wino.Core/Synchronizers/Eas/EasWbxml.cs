using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Wino.Core.Synchronizers.Eas;

internal static class EasWbxml
{
    internal static readonly XName Opaque = XName.Get("opaque", "urn:wino:eas");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Dictionary<XName, (byte Page, byte Token)> Tokens = new();
    private static readonly Dictionary<(byte Page, byte Token), XName> Names = new();

    static EasWbxml()
    {
        // Token assignments come from MS-ASWBXML, not from XML element order.
        Add(0, "AirSync", "05 Sync|06 Responses|07 Add|08 Change|09 Delete|0A Fetch|0B SyncKey|0C ClientId|0D ServerId|0E Status|0F Collection|10 Class|12 CollectionId|13 GetChanges|14 MoreAvailable|15 WindowSize|16 Commands|17 Options|18 FilterType|1B Conflict|1C Collections|1D ApplicationData|1E DeletesAsMoves|20 Supported|21 SoftDelete|22 MIMESupport|23 MIMETruncation|24 Wait|25 Limit|26 Partial|27 ConversationMode|28 MaxItems|29 HeartbeatInterval");
        Add(1, "Contacts", "05 Anniversary|06 AssistantName|07 AssistantPhoneNumber|08 Birthday|0C Business2PhoneNumber|0D BusinessAddressCity|0E BusinessAddressCountry|0F BusinessAddressPostalCode|10 BusinessAddressState|11 BusinessAddressStreet|12 BusinessFaxNumber|13 BusinessPhoneNumber|14 CarPhoneNumber|15 Categories|16 Category|17 Children|18 Child|19 CompanyName|1A Department|1B Email1Address|1C Email2Address|1D Email3Address|1E FileAs|1F FirstName|20 Home2PhoneNumber|21 HomeAddressCity|22 HomeAddressCountry|23 HomeAddressPostalCode|24 HomeAddressState|25 HomeAddressStreet|26 HomeFaxNumber|27 HomePhoneNumber|28 JobTitle|29 LastName|2A MiddleName|2B MobilePhoneNumber|2C OfficeLocation|2D OtherAddressCity|2E OtherAddressCountry|2F OtherAddressPostalCode|30 OtherAddressState|31 OtherAddressStreet|32 PagerNumber|33 RadioPhoneNumber|34 Spouse|35 Suffix|36 Title|37 WebPage|38 YomiCompanyName|39 YomiFirstName|3A YomiLastName|3C Picture|3D Alias|3E WeightedRank");
        Add(2, "Email", "0F DateReceived|11 DisplayTo|12 Importance|13 MessageClass|14 Subject|15 Read|16 To|17 Cc|18 From|19 ReplyTo|1A AllDayEvent|1B Categories|1C Category|1D DtStamp|1E EndTime|1F InstanceType|20 BusyStatus|21 Location|22 MeetingRequest|23 Organizer|24 RecurrenceId|25 Reminder|26 ResponseRequested|27 Recurrences|28 Recurrence|29 Type|2A Until|2B Occurrences|2C Interval|2D DayOfWeek|2E DayOfMonth|2F WeekOfMonth|30 MonthOfYear|31 StartTime|32 Sensitivity|33 TimeZone|34 GlobalObjId|35 ThreadTopic|39 InternetCPID|3A Flag|3B Status|3C ContentClass|3D FlagType|3E CompleteTime|3F DisallowNewTimeProposal");
        Add(4, "Calendar", "05 TimeZone|06 AllDayEvent|07 Attendees|08 Attendee|09 Email|0A Name|0D BusyStatus|0E Categories|0F Category|11 DtStamp|12 EndTime|13 Exception|14 Exceptions|15 Deleted|16 ExceptionStartTime|17 Location|18 MeetingStatus|19 OrganizerEmail|1A OrganizerName|1B Recurrence|1C Type|1D Until|1E Occurrences|1F Interval|20 DayOfWeek|21 DayOfMonth|22 WeekOfMonth|23 MonthOfYear|24 Reminder|25 Sensitivity|26 Subject|27 StartTime|28 UID|29 AttendeeStatus|2A AttendeeType|33 DisallowNewTimeProposal|34 ResponseRequested|35 AppointmentReplyTime|36 ResponseType|37 CalendarType|38 IsLeapMonth|39 FirstDayOfWeek|3A OnlineMeetingConfLink|3B OnlineMeetingExternalLink");
        Add(5, "Move", "05 MoveItems|06 Move|07 SrcMsgId|08 SrcFldId|09 DstFldId|0A Response|0B Status|0C DstMsgId");
        Add(6, "GetItemEstimate", "05 GetItemEstimate|06 Version|07 Collections|08 Collection|09 Class|0A CollectionId|0B DateTime|0C Estimate|0D Response|0E Status");
        Add(7, "FolderHierarchy", "07 DisplayName|08 ServerId|09 ParentId|0A Type|0C Status|0E Changes|0F Add|10 Delete|11 Update|12 SyncKey|13 FolderCreate|14 FolderDelete|15 FolderUpdate|16 FolderSync|17 Count");
        Add(8, "MeetingResponse", "05 CalendarId|06 CollectionId|07 MeetingResponse|08 RequestId|09 Request|0A Result|0B Status|0C UserResponse|0E InstanceId");
        Add(9, "Tasks", "08 Categories|09 Category|0A Complete|0B DateCompleted|0C DueDate|0D UtcDueDate|0E Importance|0F Recurrence|10 Type|11 Start|12 Until|13 Occurrences|14 Interval|15 DayOfMonth|16 DayOfWeek|17 WeekOfMonth|18 MonthOfYear|19 Regenerate|1A DeadOccur|1B ReminderSet|1C ReminderTime|1D Sensitivity|1E StartDate|1F UtcStartDate|20 Subject|22 OrdinalDate|23 SubOrdinalDate|24 CalendarType|25 IsLeapMonth|26 FirstDayOfWeek");
        Add(12, "Contacts2", "05 CustomerId|06 GovernmentId|07 IMAddress|08 IMAddress2|09 IMAddress3|0A ManagerName|0B CompanyMainPhone|0C AccountName|0D NickName|0E MMS");
        Add(13, "Ping", "05 Ping|06 AutdState|07 Status|08 HeartbeatInterval|09 Folders|0A Folder|0B Id|0C Class|0D MaxFolders");
        Add(14, "Provision", "05 Provision|06 Policies|07 Policy|08 PolicyType|09 PolicyKey|0A Data|0B Status|0C RemoteWipe|0D EASProvisionDoc|0E DevicePasswordEnabled|0F AlphanumericDevicePasswordRequired|10 RequireStorageCardEncryption|11 PasswordRecoveryEnabled|13 AttachmentsEnabled|14 MinDevicePasswordLength|15 MaxInactivityTimeDeviceLock|16 MaxDevicePasswordFailedAttempts|17 MaxAttachmentSize|18 AllowSimpleDevicePassword|19 DevicePasswordExpiration|1A DevicePasswordHistory|1B AllowStorageCard|1C AllowCamera|1D RequireDeviceEncryption|1E AllowUnsignedApplications|1F AllowUnsignedInstallationPackages|20 MinDevicePasswordComplexCharacters|21 AllowWiFi|22 AllowTextMessaging|23 AllowPOPIMAPEmail|24 AllowBluetooth|25 AllowIrDA|26 RequireManualSyncWhenRoaming|27 AllowDesktopSync|28 MaxCalendarAgeFilter|29 AllowHTMLEmail|2A MaxEmailAgeFilter|2B MaxEmailBodyTruncationSize|2C MaxEmailHTMLBodyTruncationSize|2D RequireSignedSMIMEMessages|2E RequireEncryptedSMIMEMessages|2F RequireSignedSMIMEAlgorithm|30 RequireEncryptionSMIMEAlgorithm|31 AllowSMIMEEncryptionAlgorithmNegotiation|32 AllowSMIMESoftCerts|33 AllowBrowser|34 AllowConsumerEmail|35 AllowRemoteDesktop|36 AllowInternetSharing|37 UnapprovedInROMApplicationList|38 ApplicationName|39 ApprovedApplicationList|3A Hash");
        Add(15, "Search", "05 Search|07 Store|08 Name|09 Query|0A Options|0B Range|0C Status|0D Response|0E Result|0F Properties|10 Total|11 EqualTo|12 Value|13 And|14 Or|15 FreeText|17 DeepTraversal|18 LongId|19 RebuildResults|1A LessThan|1B GreaterThan|1E UserName|1F Password|20 ConversationId|21 Picture|22 MaxSize|23 MaxPictures");
        Add(17, "AirSyncBase", "05 BodyPreference|06 Type|07 TruncationSize|08 AllOrNone|0A Body|0B Data|0C EstimatedDataSize|0D Truncated|0E Attachments|0F Attachment|10 DisplayName|11 FileReference|12 Method|13 ContentId|14 ContentLocation|15 IsInline|16 NativeBodyType|17 ContentType|18 Preview|19 BodyPartPreference|1A BodyPart|1B Status");
        Add(18, "Settings", "05 Settings|06 Status|07 Get|08 Set|09 Oof|0A OofState|0B StartTime|0C EndTime|0D OofMessage|0E AppliesToInternal|0F AppliesToExternalKnown|10 AppliesToExternalUnknown|11 Enabled|12 ReplyMessage|13 BodyType|14 DevicePassword|15 Password|16 DeviceInformation|17 Model|18 IMEI|19 FriendlyName|1A OS|1B OSLanguage|1C PhoneNumber|1D UserInformation|1E EmailAddresses|1F SMTPAddress|20 UserAgent|21 EnableOutboundSMS|22 MobileOperator|23 PrimarySmtpAddress|24 Accounts|25 Account|26 AccountId|27 AccountName|28 UserDisplayName|29 SendDisabled|2B RightsManagementInformation");
        Add(20, "ItemOperations", "05 ItemOperations|06 Fetch|07 Store|08 Options|09 Range|0A Total|0B Properties|0C Data|0D Status|0E Response|0F Version|10 Schema|11 Part|12 EmptyFolderContents|13 DeleteSubFolders|14 UserName|15 Password|16 Move|17 DstFldId|18 ConversationId|19 MoveAlways");
        Add(21, "ComposeMail", "05 SendMail|06 SmartForward|07 SmartReply|08 SaveInSentItems|09 ReplaceMime|0B Source|0C FolderId|0D ItemId|0E LongId|0F InstanceId|10 Mime|11 ClientId|12 Status|13 AccountId");
        Add(22, "Email2", "05 UmCallerID|06 UmUserNotes|07 UmAttDuration|08 UmAttOrder|09 ConversationId|0A ConversationIndex|0B LastVerbExecuted|0C LastVerbExecutionTime|0D ReceivedAsBcc|0E Sender|0F CalendarType|10 IsLeapMonth|11 AccountId|12 FirstDayOfWeek|13 MeetingMessageType");
        Add(24, "RightsManagement", "05 RightsManagementSupport|06 RightsManagementTemplates|07 RightsManagementTemplate|08 RightsManagementLicense|09 EditAllowed|0A ReplyAllowed|0B ReplyAllAllowed|0C ForwardAllowed|0D ModifyRecipientsAllowed|0E ExtractAllowed|0F PrintAllowed|10 ExportAllowed|11 ProgrammaticAccessAllowed|12 Owner|13 ContentExpiryDate|14 TemplateID|15 TemplateName|16 TemplateDescription|17 ContentOwner|18 RemoveRightsManagementDistribution");
    }

    private static void Add(byte page, string ns, string entries)
    {
        foreach (var entry in entries.Split('|'))
        {
            var parts = entry.Split(' ');
            var token = byte.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var name = XName.Get(parts[1], ns + ":");
            Tokens.Add(name, (page, token));
            Names.Add((page, token), name);
        }
    }

    internal static byte[] Encode(XElement root)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 3, 1, 106, 0 });
        byte page = 0;
        WriteElement(stream, root, ref page, 0);
        return stream.ToArray();
    }

    private static void WriteElement(Stream stream, XElement element, ref byte currentPage, int depth)
    {
        if (depth > 64 || !Tokens.TryGetValue(element.Name, out var tag))
            throw new InvalidDataException("Unsupported EAS XML element or excessive nesting.");

        if (element.HasAttributes && element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute.Name != Opaque))
            throw new InvalidDataException("EAS WBXML does not allow XML attributes.");

        if (tag.Page != currentPage)
        {
            stream.WriteByte(0);
            stream.WriteByte(tag.Page);
            currentPage = tag.Page;
        }

        var opaque = element.Attribute(Opaque) != null;
        var hasContent = opaque || element.Nodes().Any();
        stream.WriteByte((byte)(tag.Token | (hasContent ? 0x40 : 0)));
        if (opaque)
        {
            var bytes = Convert.FromBase64String(element.Value);
            stream.WriteByte(0xC3);
            WriteInteger(stream, bytes.Length);
            stream.Write(bytes);
            stream.WriteByte(1);
            return;
        }
        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
                WriteElement(stream, child, ref currentPage, depth + 1);
            else if (node is XText text)
            {
                if (text.Value.Contains('\0'))
                    throw new InvalidDataException("EAS text cannot contain a null character.");

                stream.WriteByte(3);
                stream.Write(Utf8.GetBytes(text.Value));
                stream.WriteByte(0);
            }
            else
                throw new InvalidDataException("Unsupported EAS XML node.");
        }

        if (hasContent)
            stream.WriteByte(1);
    }

    internal static XElement Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        if (ReadByte(stream) != 3 || ReadInteger(stream) != 1 || ReadInteger(stream) != 106 || ReadInteger(stream) != 0)
            throw new InvalidDataException("Invalid EAS WBXML header.");

        byte page = 0;
        XElement? root = null;
        var stack = new Stack<XElement>();
        var elements = 0;
        while (stream.Position < stream.Length)
        {
            var token = ReadByte(stream);
            if (token == 0)
            {
                page = ReadByte(stream);
                continue;
            }

            if (token == 1)
            {
                if (stack.Count == 0)
                    throw new InvalidDataException("Unbalanced EAS WBXML end token.");

                stack.Pop();
                continue;
            }

            if (token is 3 or 0xC3)
            {
                if (stack.Count == 0)
                    throw new InvalidDataException("EAS WBXML text has no parent element.");

                if (token == 3)
                {
                    using var text = new MemoryStream();
                    byte value;
                    while ((value = ReadByte(stream)) != 0)
                        text.WriteByte(value);

                    stack.Peek().Add(new XText(Utf8.GetString(text.ToArray())));
                }
                else
                {
                    var length = ReadInteger(stream);
                    if (length > stream.Length - stream.Position)
                        throw new InvalidDataException("Truncated EAS WBXML opaque value.");

                    var data = new byte[length];
                    stream.ReadExactly(data);
                    var current = stack.Peek();
                    current.SetAttributeValue(Opaque, "base64");
                    current.Add(new XText(Convert.ToBase64String(data)));
                }

                continue;
            }

            if ((token & 0x80) != 0 || !Names.TryGetValue((page, (byte)(token & 0x3F)), out var name))
                throw new InvalidDataException("Unknown EAS WBXML token.");

            if (++elements > 500000 || stack.Count >= 64)
                throw new InvalidDataException("EAS WBXML exceeds the document complexity limit.");

            var element = new XElement(name);
            if (stack.Count > 0)
                stack.Peek().Add(element);
            else if (root == null)
                root = element;
            else
                throw new InvalidDataException("EAS WBXML has multiple root elements.");

            if ((token & 0x40) != 0)
                stack.Push(element);
        }

        if (root == null || stack.Count != 0)
            throw new InvalidDataException("Incomplete EAS WBXML document.");

        return root;
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new InvalidDataException("Truncated EAS WBXML document.") : (byte)value;
    }

    private static int ReadInteger(Stream stream)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            var next = ReadByte(stream);
            if (value > (int.MaxValue >> 7))
                throw new InvalidDataException("EAS WBXML integer overflow.");

            value = (value << 7) | (next & 0x7F);
            if ((next & 0x80) == 0)
                return value;
        }

        throw new InvalidDataException("Invalid EAS WBXML integer.");
    }

    private static void WriteInteger(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var index = buffer.Length - 1;
        buffer[index] = (byte)(value & 0x7F);
        while ((value >>= 7) != 0)
            buffer[--index] = (byte)((value & 0x7F) | 0x80);
        stream.Write(buffer[index..]);
    }
}
