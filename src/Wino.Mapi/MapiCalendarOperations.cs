using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>The store's tags for the appointment named properties, resolved once per session.</summary>
public sealed record MapiCalendarTags(
    uint StartWhole, uint EndWhole, uint Location, uint AllDay, uint BusyStatus, uint Recurring, uint AppointmentRecur,
    uint ResponseStatus, uint StateFlags, uint TimeZoneDefinitionStart,
    uint ReminderSet, uint ReminderDelta,
    uint GlobalObjectId,
    uint CleanGlobalObjectId, uint OwnerCriticalChange, uint AttendeeCriticalChange, uint Where,
    uint AppointmentSequence, uint AppointmentReplyTime, uint IntendedBusyStatus, uint AppointmentReplyName,
    uint ClientTrackingId,
    uint FInvited = 0, uint AppointmentDuration = 0,
    uint TimeZoneDefinitionEnd = 0, uint TimeZoneDefinitionRecur = 0, uint TimeZoneStruct = 0, uint TimeZoneDescription = 0,
    uint RecurrenceType = 0, uint ClipStart = 0, uint ClipEnd = 0,
    uint ExceptionReplaceTime = 0,
    uint IsRecurring = 0, uint IsException = 0);

/// <summary>One row of a Calendar folder: a single appointment, or the master of a series (with its recurrence).</summary>
public sealed record MapiAppointmentInfo(
    ulong MessageId,
    string MessageClass,
    string? Subject,
    string? Body,
    string? Location,
    DateTime? StartUtc,
    DateTime? EndUtc,
    bool AllDay,
    uint? BusyStatus,
    bool IsRecurring,
    uint? Sensitivity,
    uint? ResponseStatus,
    uint? StateFlags,
    string? OrganizerName,
    string? OrganizerAddress,
    DateTime? Created,
    DateTime? Modified,
    bool ReminderSet,
    uint? ReminderMinutes,
    string? ClientTrackingId,
    string? TimeZoneKeyName,
    byte[]? CleanGlobalObjectId = null)
{
    public bool IsAppointment => MessageClass.StartsWith(PropertyTags.AppointmentMessageClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>PidLidAppointmentStateFlags: 0x1 meeting, 0x2 received (attendee), 0x4 cancelled.</summary>
    public bool IsMeeting => ((StateFlags ?? 0) & 0x1) != 0;
    public bool IsReceived => ((StateFlags ?? 0) & 0x2) != 0;
    public bool IsCancelled => ((StateFlags ?? 0) & 0x4) != 0;

    /// <summary>The series recurrence, decoded for masters (inline or streamed).</summary>
    public AppointmentRecurrence? Recurrence { get; set; }

    /// <summary>The recipient table, read for meetings by <see cref="MapiCalendarOperations.ReadAttendeesAsync"/>; null when not read.</summary>
    public List<OpenRecipient>? Attendees { get; set; }
}

/// <summary>
/// Calendar over MAPI: the Calendar folder by its well-known entry id, rows from its contents
/// table with the MS-OXOCAL named properties, and the recurrence blob for series masters. Recurrence is
/// expanded by the caller (the server does not expand a contents table the way an EWS CalendarView did).
/// </summary>
public static class MapiCalendarOperations
{
    private const int Slots = 3;

    public static async Task<MapiCalendarTags> ResolveTagsAsync(MapiSession session, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var names = new List<RopProperties.PropertyName>
        {
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentStartWhole),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentEndWhole),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidLocation),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentSubType),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidBusyStatus),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidRecurring),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentRecur),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidResponseStatus),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentStateFlags),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentTimeZoneDefinitionStartDisplay),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidReminderSet),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidReminderDelta),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidGlobalObjectId),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidCleanGlobalObjectId),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidOwnerCriticalChange),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidAttendeeCriticalChange),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidWhere),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentSequence),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentReplyTime),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidIntendedBusyStatus),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentReplyName),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidFInvited),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentDuration),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentTimeZoneDefinitionEndDisplay),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidAppointmentTimeZoneDefinitionRecur),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidTimeZoneStruct),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidTimeZoneDescription),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidRecurrenceType),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidClipStart),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidClipEnd),
            RopProperties.PropertyName.ById(PropertyTags.AppointmentPropertySet, PropertyTags.LidExceptionReplaceTime),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidIsRecurring),
            RopProperties.PropertyName.ById(PropertyTags.MeetingPropertySet, PropertyTags.LidIsException),
        };
        ushort[] types =
        [
            PropertyTypes.SysTime, PropertyTypes.SysTime, PropertyTypes.Unicode, PropertyTypes.Boolean, PropertyTypes.Long, PropertyTypes.Boolean, PropertyTypes.Binary,
            PropertyTypes.Long, PropertyTypes.Long, PropertyTypes.Binary,
            PropertyTypes.Boolean, PropertyTypes.Long,
            PropertyTypes.Binary,
            PropertyTypes.Binary, PropertyTypes.SysTime, PropertyTypes.SysTime, PropertyTypes.Unicode,
            PropertyTypes.Long, PropertyTypes.SysTime, PropertyTypes.Long, PropertyTypes.Unicode,
            PropertyTypes.Boolean, PropertyTypes.Long,
            PropertyTypes.Binary, PropertyTypes.Binary, PropertyTypes.Binary, PropertyTypes.Unicode,
            PropertyTypes.Long, PropertyTypes.SysTime, PropertyTypes.SysTime,
            PropertyTypes.SysTime,
            PropertyTypes.Boolean, PropertyTypes.Boolean,
        ];

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 1);
        var (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertyIdsFromNames(0, names), handles, cancellationToken).ConfigureAwait(false);
        var ids = RopProperties.ParseGetPropertyIdsFromNames(new RopReader(rops));
        if (ids.Count != names.Count || ids.Any(id => id == 0))
            throw new MapiFormatException("The store did not map every appointment named property.");

        uint Tag(int i) => ((uint)ids[i] << 16) | types[i];

        // The client tracking id is a string-named property in PS_PUBLIC_STRINGS. Resolved on its own so a
        // refusal of the string form (ecRpcFormat) costs the tracking id, not the calendar; the request
        // bytes are logged for the encoding to be corrected against.
        uint trackingTag = 0;
        var trackingRequest = RopProperties.BuildGetPropertyIdsFromNames(0, [RopProperties.PropertyName.ByName(PropertyTags.PublicStringsPropertySet, PropertyTags.ClientTrackingIdPropertyName)]);
        try
        {
            (rops, _) = await session.ExecuteAsync(trackingRequest, handles, cancellationToken).ConfigureAwait(false);
            var trackingIds = RopProperties.ParseGetPropertyIdsFromNames(new RopReader(rops));
            if (trackingIds.Count == 1 && trackingIds[0] != 0)
                trackingTag = ((uint)trackingIds[0] << 16) | PropertyTypes.Unicode;
        }
        catch (MapiException ex)
        {
            diagnostics?.Invoke($"client tracking id property not resolved ({ex.Message}); request was:" + "\n" + HexDump.Render(trackingRequest, 256));
        }

        return new MapiCalendarTags(Tag(0), Tag(1), Tag(2), Tag(3), Tag(4), Tag(5), Tag(6), Tag(7), Tag(8), Tag(9), Tag(10), Tag(11), Tag(12),
            Tag(13), Tag(14), Tag(15), Tag(16), Tag(17), Tag(18), Tag(19), Tag(20), trackingTag, Tag(21), Tag(22),
            Tag(23), Tag(24), Tag(25), Tag(26), Tag(27), Tag(28), Tag(29), Tag(30), Tag(31), Tag(32));
    }

    public static Task<ulong?> FindCalendarFolderIdAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default)
        => MapiContactOperations.FindWellKnownFolderIdAsync(session, inboxFolderId, PropertyTags.IpmAppointmentEntryId, cancellationToken);

    private static uint[] Columns(MapiCalendarTags tags)
    {
        var columns = new List<uint>
        {
            PropertyTags.Mid, PropertyTags.MessageClass, PropertyTags.Subject, PropertyTags.Body, tags.Location,
            tags.StartWhole, tags.EndWhole, tags.AllDay, tags.BusyStatus, tags.Recurring,
            PropertyTags.Sensitivity, tags.ResponseStatus, tags.StateFlags,
            PropertyTags.SentRepresentingName, PropertyTags.SentRepresentingSmtpAddress,
            PropertyTags.CreationTime, PropertyTags.LastModificationTime,
            tags.ReminderSet, tags.ReminderDelta, tags.TimeZoneDefinitionStart, tags.AppointmentRecur, tags.CleanGlobalObjectId,
        };
        if (tags.ClientTrackingId != 0)
            columns.Add(tags.ClientTrackingId);
        return columns.ToArray();
    }

    /// <summary>
    /// Reads every row of the folder. Series masters come back with their recurrence decoded (fetched
    /// by stream when the table could not carry it inline). Rows the client cannot decode are skipped
    /// and reported through <paramref name="diagnostics"/> rather than failing the sweep.
    /// </summary>
    public static async Task<List<MapiAppointmentInfo>> ReadAppointmentsAsync(MapiSession session, ulong folderId, MapiCalendarTags tags, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var columns = Columns(tags);
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        var replicas = RopFolder.ParseOpenFolder(new RopReader(rops));
        if (replicas.Count > 0) diagnostics?.Invoke($"folder 0x{folderId:X16} is ghosted; replicas: {string.Join(", ", replicas)}");
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        var appointments = new List<MapiAppointmentInfo>();
        var recurrencePending = new List<MapiAppointmentInfo>();
        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetContentsTable(1, 2, RopFolder.TableFlags.DeferredErrors), handles, cancellationToken).ConfigureAwait(false);
            var rowCount = RopFolder.ParseGetContentsTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, columns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 20), handles, cancellationToken).ConfigureAwait(false);
                    List<PropertyValue[]> rows;
                    try
                    {
                        rows = RopFolder.ParseQueryRows(new RopReader(rops), columns);
                    }
                    catch (MapiRopException ex)
                    {
                        diagnostics?.Invoke($"RopQueryRows on 0x{folderId:X16} failed ({ex.Message}); response:" + Environment.NewLine + HexDump.Render(rops, 512));
                        throw replicas.Count > 0 ? new MapiGhostedFolderException(replicas, ex) : ex;
                    }
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        if (row[0].AsUInt64 is not { } mid)
                            continue;

                        PropertyValue Cell(uint tag) => Array.Find(row, c => c.Tag == tag);

                        var info = new MapiAppointmentInfo(
                            mid,
                            row[1].AsString ?? string.Empty,
                            row[2].AsString,
                            row[3].AsString,
                            row[4].AsString,
                            row[5].Value as DateTime?,
                            row[6].Value as DateTime?,
                            row[7].AsBoolean ?? false,
                            row[8].AsUInt32,
                            row[9].AsBoolean ?? false,
                            row[10].AsUInt32,
                            row[11].AsUInt32,
                            row[12].AsUInt32,
                            row[13].AsString,
                            row[14].AsString,
                            row[15].Value as DateTime?,
                            row[16].Value as DateTime?,
                            row[17].AsBoolean ?? false,
                            row[18].AsUInt32,
                            tags.ClientTrackingId != 0 ? Cell(tags.ClientTrackingId).AsString : null,
                            TimeZoneKeyName(row[19].AsBinary),
                            row[21].AsBinary);

                        if (info.IsRecurring)
                        {
                            var recur = row[20];
                            if (recur.AsBinary is { Length: > 0 } inline)
                            {
                                try { info.Recurrence = AppointmentRecurrence.Parse(inline); }
                                catch (MapiFormatException ex) { diagnostics?.Invoke($"appointment 0x{mid:X16}: recurrence not decoded ({ex.Message})"); }
                            }
                            else if (recur.Error == MapiRopException.NotEnoughMemory)
                            {
                                recurrencePending.Add(info);
                            }
                        }

                        appointments.Add(info);
                    }
                }

                diagnostics?.Invoke($"calendar 0x{folderId:X16}: {appointments.Count} rows (RowCount {rowCount}), {appointments.Count(a => a.IsRecurring)} recurring, {recurrencePending.Count} recurrence blobs to stream");
            }
            finally
            {
                await session.ReleaseAsync(handles[2], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        foreach (var master in recurrencePending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var blob = await MapiRulesOperations.ReadPropertyStreamAsync(session, folderId, master.MessageId, tags.AppointmentRecur, cancellationToken).ConfigureAwait(false);
                master.Recurrence = AppointmentRecurrence.Parse(blob);
            }
            catch (MapiException ex)
            {
                diagnostics?.Invoke($"appointment 0x{master.MessageId:X16}: recurrence not read ({ex.Message})");
            }
        }

        return appointments;
    }

    /// <summary>
    /// The recipient table of an appointment (attendees with their response status, the organizer
    /// flagged), from the RopOpenMessage response. One round trip per meeting.
    /// </summary>
    public static async Task<List<OpenRecipient>> ReadAttendeesAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            var response = RopMessage.ParseOpenMessageResponse(new RopReader(rops), diagnostics);
            return response.Recipients;
        }
        catch (MapiFormatException ex)
        {
            diagnostics?.Invoke($"RopOpenMessage response not decoded ({ex.Message}); bytes:" + Environment.NewLine + HexDump.Render(rops, 2048));
            throw;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    public enum MeetingResponse { Accept, Tentative, Decline }

    /// <summary>What a response message needs from the meeting it answers (MS-OXOCAL 2.2.7).</summary>
    public sealed record MeetingIdentity(byte[]? GlobalObjectId, byte[]? CleanGlobalObjectId, DateTime? OwnerCriticalChange, uint Sequence,
        DateTime? StartUtc, DateTime? EndUtc, string? Location, string? Subject, uint? IntendedBusyStatus, string? OrganizerAddress, string? OrganizerName,
        List<OpenRecipient>? Attendees = null, bool AllDay = false);

    public static async Task<MeetingIdentity> ReadMeetingIdentityAsync(MapiSession session, ulong folderId, ulong messageId, MapiCalendarTags tags, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        uint[] props = [tags.GlobalObjectId, tags.CleanGlobalObjectId, tags.OwnerCriticalChange, tags.AppointmentSequence, tags.StartWhole, tags.EndWhole, tags.Location, PropertyTags.Subject, tags.IntendedBusyStatus, PropertyTags.SentRepresentingSmtpAddress, PropertyTags.SentRepresentingName, tags.AllDay];

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            var open = RopMessage.ParseOpenMessageResponse(new RopReader(rops), diagnostics);
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, props), handles, cancellationToken).ConfigureAwait(false);
            var v = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), props);

            var organizer = open.Recipients.FirstOrDefault(r => r.IsOrganizer);
            return new MeetingIdentity(
                v[0].AsBinary, v[1].AsBinary, v[2].Value as DateTime?, v[3].AsUInt32 ?? 0,
                v[4].Value as DateTime?, v[5].Value as DateTime?, v[6].AsString, v[7].AsString, v[8].AsUInt32,
                organizer?.SmtpAddress ?? v[9].AsString, organizer?.Name ?? v[10].AsString,
                open.Recipients, v[11].AsBoolean ?? false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The iCalendar text a meeting message would carry on the wire (RFC 5545 / MS-OXCICAL), built from
    /// the meeting's properties so the app's invitation handling sees what an EWS MIME download gave it.
    /// UID is the clean global object id in hex, which is how Exchange itself forms it.
    /// </summary>
    public static string BuildICalendar(MeetingIdentity meeting, string method, DateTime nowUtc)
    {
        var uid = (meeting.CleanGlobalObjectId ?? meeting.GlobalObjectId) is { Length: > 0 } goid ? Convert.ToHexString(goid) : Guid.NewGuid().ToString("N");
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "PRODID:-//Wino//MAPI//EN",
            "VERSION:2.0",
            "METHOD:" + method,
            "BEGIN:VEVENT",
            "UID:" + uid,
            "DTSTAMP:" + Stamp(nowUtc),
            "SEQUENCE:" + meeting.Sequence,
        };
        if (meeting.StartUtc is { } start) lines.Add(meeting.AllDay ? "DTSTART;VALUE=DATE:" + start.ToString("yyyyMMdd") : "DTSTART:" + Stamp(start));
        if (meeting.EndUtc is { } end) lines.Add(meeting.AllDay ? "DTEND;VALUE=DATE:" + end.ToString("yyyyMMdd") : "DTEND:" + Stamp(end));
        lines.Add("SUMMARY:" + Escape(meeting.Subject));
        if (!string.IsNullOrEmpty(meeting.Location)) lines.Add("LOCATION:" + Escape(meeting.Location));
        if (!string.IsNullOrEmpty(meeting.OrganizerAddress)) lines.Add($"ORGANIZER;CN={Quote(meeting.OrganizerName ?? meeting.OrganizerAddress)}:mailto:{meeting.OrganizerAddress}");
        foreach (var r in meeting.Attendees ?? [])
        {
            if (r.IsOrganizer || string.IsNullOrEmpty(r.SmtpAddress)) continue;
            var partstat = r.TrackStatus switch { 2 => "TENTATIVE", 3 => "ACCEPTED", 4 => "DECLINED", _ => "NEEDS-ACTION" };
            var role = r.IsOptional ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT";
            lines.Add($"ATTENDEE;CN={Quote(r.Name ?? r.SmtpAddress)};ROLE={role};PARTSTAT={partstat};RSVP=TRUE:mailto:{r.SmtpAddress}");
        }
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines) + "\r\n";

        static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'");
        static string Escape(string? text) => (text ?? string.Empty).Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
        static string Quote(string text) => "\"" + text.Replace("\"", string.Empty) + "\"";
    }

    /// <summary>The METHOD a meeting message class maps to; null for classes that are not meeting messages.</summary>
    public static string? MeetingMethod(string? messageClass)
    {
        if (messageClass is null) return null;
        if (messageClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase)) return "REQUEST";
        if (messageClass.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase)) return "CANCEL";
        if (messageClass.StartsWith("IPM.Schedule.Meeting.Resp", StringComparison.OrdinalIgnoreCase)) return "REPLY";
        return null;
    }

    /// <summary>The properties stamped on the appointment when the attendee responds (MS-OXOCAL 3.1.4.8.1).</summary>
    public static List<TaggedPropertyValue> ResponseAppointmentProperties(MapiCalendarTags tags, MeetingResponse response, uint? intendedBusyStatus, string replyName, DateTime nowUtc)
    {
        var values = new List<TaggedPropertyValue>
        {
            TaggedPropertyValue.Long(tags.ResponseStatus, response switch { MeetingResponse.Accept => 3u, MeetingResponse.Tentative => 2u, _ => 4u }),
            TaggedPropertyValue.SysTime(tags.AppointmentReplyTime, nowUtc),
            TaggedPropertyValue.Unicode(tags.AppointmentReplyName, replyName),
        };
        if (response == MeetingResponse.Accept)
            values.Add(TaggedPropertyValue.Long(tags.BusyStatus, intendedBusyStatus ?? 2));
        else if (response == MeetingResponse.Tentative)
            values.Add(TaggedPropertyValue.Long(tags.BusyStatus, 1));
        return values;
    }

    /// <summary>The response message (MS-OXOCAL 2.2.7): class by response, identity copied from the meeting, sent to the organizer.</summary>
    public static MapiOutgoingMessage BuildResponseMessage(MapiCalendarTags tags, MeetingResponse response, MeetingIdentity meeting, string? comment, DateTime nowUtc)
    {
        var (messageClass, prefix) = response switch
        {
            MeetingResponse.Accept => (PropertyTags.MeetingResponseAcceptClass, "Accepted: "),
            MeetingResponse.Tentative => (PropertyTags.MeetingResponseTentativeClass, "Tentative: "),
            _ => (PropertyTags.MeetingResponseDeclineClass, "Declined: "),
        };

        var message = new MapiOutgoingMessage
        {
            MessageClass = messageClass,
            Subject = prefix + (meeting.Subject ?? string.Empty),
            TextBody = string.IsNullOrWhiteSpace(comment) ? null : comment,
        };
        if (!string.IsNullOrEmpty(meeting.OrganizerAddress))
            message.Recipients.Add(new RopMessageWrite.Recipient(RopMessageWrite.RecipientType.To, meeting.OrganizerName ?? meeting.OrganizerAddress, meeting.OrganizerAddress));

        var extra = message.ExtraProperties;
        if (meeting.GlobalObjectId is { Length: > 0 } goid) extra.Add(TaggedPropertyValue.Binary(tags.GlobalObjectId, goid));
        if (meeting.CleanGlobalObjectId is { Length: > 0 } clean) extra.Add(TaggedPropertyValue.Binary(tags.CleanGlobalObjectId, clean));
        if (meeting.OwnerCriticalChange is { } owner) extra.Add(TaggedPropertyValue.SysTime(tags.OwnerCriticalChange, owner));
        extra.Add(TaggedPropertyValue.SysTime(tags.AttendeeCriticalChange, nowUtc));
        extra.Add(TaggedPropertyValue.Long(tags.AppointmentSequence, meeting.Sequence));
        if (meeting.StartUtc is { } start) extra.Add(TaggedPropertyValue.SysTime(tags.StartWhole, start));
        if (meeting.EndUtc is { } end) extra.Add(TaggedPropertyValue.SysTime(tags.EndWhole, end));
        if (!string.IsNullOrEmpty(meeting.Location)) { extra.Add(TaggedPropertyValue.Unicode(tags.Location, meeting.Location)); extra.Add(TaggedPropertyValue.Unicode(tags.Where, meeting.Location)); }
        return message;
    }

    /// <summary>
    /// Responds to a meeting as the attendee: stamps the appointment, sends the response to the
    /// organizer through the Outbox, and on a decline moves the appointment to Deleted Items.
    /// </summary>
    public static async Task RespondToMeetingAsync(MapiSession session, ulong calendarFolderId, ulong messageId, ulong outboxFolderId, ulong sentItemsFolderId, ulong deletedItemsFolderId,
        MapiCalendarTags tags, MeetingResponse response, string? comment, string replyName, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var meeting = await ReadMeetingIdentityAsync(session, calendarFolderId, messageId, tags, cancellationToken, diagnostics).ConfigureAwait(false);
        if (string.IsNullOrEmpty(meeting.OrganizerAddress))
            throw new MapiFormatException("The meeting has no organizer address to respond to.");

        var now = DateTime.UtcNow;

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(calendarFolderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(1, ResponseAppointmentProperties(tags, response, meeting.IntendedBusyStatus, replyName, now)), handles, cancellationToken).ConfigureAwait(false);
            RopMessageOps.ParseSetProperties(new RopReader(rops));
            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
            RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        var message = BuildResponseMessage(tags, response, meeting, comment, now);
        await MapiMessageComposer.SendAsync(session, outboxFolderId, sentItemsFolderId, message, cancellationToken, diagnostics).ConfigureAwait(false);
        diagnostics?.Invoke($"meeting 0x{messageId:X16}: {response} sent to {meeting.OrganizerAddress}");

        if (response == MeetingResponse.Decline)
            await MapiMessageOperations.MoveAsync(session, calendarFolderId, deletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The fields this client writes to a single (non-recurring) appointment.</summary>
    public sealed class AppointmentWrite
    {
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Location { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public bool AllDay { get; set; }
        public uint BusyStatus { get; set; } = 2;
        public uint Sensitivity { get; set; }
        public int? ReminderMinutes { get; set; }
        public string? ClientTrackingId { get; set; }

        /// <summary>
        /// The zone the appointment is scheduled in (Windows or IANA id; the machine's zone when null).
        /// Written as the TZDEFINITION/TZREG blobs so the row reads back with a zone, as Outlook's do.
        /// </summary>
        public string? TimeZoneId { get; set; }

        /// <summary>
        /// The series rule (RFC 5545 RRULE, with or without its prefix) when the appointment repeats;
        /// StartUtc/EndUtc are then the first occurrence. Null for a single appointment.
        /// </summary>
        public string? RecurrenceRule { get; set; }

        public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceRule);

        /// <summary>
        /// The series' current pattern when a series is being rewritten; its deleted and changed
        /// occurrences are carried over when the new rule describes the same pattern.
        /// </summary>
        public AppointmentRecurrence? ExistingRecurrence { get; set; }

        /// <summary>The people invited; a meeting when non-empty. The organizer is not listed here.</summary>
        public List<MeetingAttendee> Attendees { get; } = [];
        public string? OrganizerName { get; set; }
        public string? OrganizerAddress { get; set; }

        public bool IsMeeting => Attendees.Count > 0;
        public uint DurationMinutes => (uint)Math.Max(0, (long)(EndUtc - StartUtc).TotalMinutes);
    }

    public sealed record MeetingAttendee(string? Name, string SmtpAddress, bool IsOptional);

    /// <summary>Creates an IPM.Appointment in the folder; returns its message id.</summary>
    public static async Task<ulong> CreateAppointmentAsync(MapiSession session, ulong folderId, MapiCalendarTags tags, AppointmentWrite appointment, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, folderId), handles, cancellationToken).ConfigureAwait(false);
        var createdId = RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            var savedId = await SetAndSaveAsync(session, handles, tags, appointment, includeClass: true, cancellationToken).ConfigureAwait(false);
            return savedId != 0 ? savedId : createdId ?? 0;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task UpdateAppointmentAsync(MapiSession session, ulong folderId, ulong messageId, MapiCalendarTags tags, AppointmentWrite appointment, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await SetAndSaveAsync(session, handles, tags, appointment, includeClass: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<ulong> SetAndSaveAsync(MapiSession session, uint[] handles, MapiCalendarTags tags, AppointmentWrite appointment, bool includeClass, CancellationToken cancellationToken)
        => SetAndSaveAsync(session, handles, Properties(tags, appointment, includeClass), null, cancellationToken);

    private static async Task<ulong> SetAndSaveAsync(MapiSession session, uint[] handles, IReadOnlyList<TaggedPropertyValue> values, IReadOnlyList<RopMessageWrite.Recipient>? recipients, CancellationToken cancellationToken,
        bool clearRecipientsFirst = false, Action<string>? diagnostics = null)
    {
        var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(1, values), handles, cancellationToken).ConfigureAwait(false);
        var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
        if (problems.Count > 0)
            throw new MapiFormatException($"The store refused appointment properties: {string.Join(", ", problems.Select(p => $"0x{p.Tag:X8}=0x{p.Error:X8}"))}");

        if (recipients is not null)
        {
            // A saved message already has a recipient table; RopModifyRecipients over it came back
            // ecRpcFormat on the box, so the table is emptied first and written the way a new one is.
            if (clearRecipientsFirst)
            {
                (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildRemoveAllRecipients(1), handles, cancellationToken).ConfigureAwait(false);
                RopMessageWrite.ParseRemoveAllRecipients(new RopReader(rops));
            }

            var modify = RopMessageWrite.BuildModifyRecipients(1, recipients);
            (rops, _) = await session.ExecuteAsync(modify, handles, cancellationToken).ConfigureAwait(false);
            try
            {
                RopMessageWrite.ParseModifyRecipients(new RopReader(rops));
            }
            catch (MapiRopException ex)
            {
                diagnostics?.Invoke($"RopModifyRecipients refused ({ex.Message}); request:" + Environment.NewLine + HexDump.Render(modify, 2048) + Environment.NewLine + "response:" + Environment.NewLine + HexDump.Render(rops, 512));
                throw;
            }
        }

        (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
        return RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
    }

    // ---- Meetings the account organizes (MS-OXOCAL 3.1.4.4 / 3.1.4.5 / 3.1.4.6) -------------------------

    /// <summary>
    /// The organizer's recipient table: the organizer as a sendable organizer row, then required
    /// attendees as To and optional ones as Cc, all sendable with no tracking status yet.
    /// </summary>
    public static List<RopMessageWrite.Recipient> MeetingRecipients(AppointmentWrite appointment, bool includeOrganizer)
    {
        var rows = new List<RopMessageWrite.Recipient>();
        if (includeOrganizer && !string.IsNullOrEmpty(appointment.OrganizerAddress))
            rows.Add(new RopMessageWrite.Recipient(RopMessageWrite.RecipientType.To, appointment.OrganizerName ?? appointment.OrganizerAddress, appointment.OrganizerAddress, Flags: 0x3, TrackStatus: 0));
        foreach (var a in appointment.Attendees)
        {
            if (string.IsNullOrWhiteSpace(a.SmtpAddress)) continue;
            rows.Add(new RopMessageWrite.Recipient(a.IsOptional ? RopMessageWrite.RecipientType.Cc : RopMessageWrite.RecipientType.To, a.Name ?? a.SmtpAddress, a.SmtpAddress, Flags: 0x1, TrackStatus: 0));
        }
        return rows;
    }

    /// <summary>
    /// What turns an appointment into the organizer's copy of a meeting: meeting state, organized
    /// response status, the global object ids, the critical-change stamps, the sequence, and the
    /// "invitations sent" mark.
    /// </summary>
    public static List<TaggedPropertyValue> OrganizerProperties(MapiCalendarTags tags, byte[] globalObjectId, uint sequence, DateTime nowUtc, uint durationMinutes)
    {
        var values = new List<TaggedPropertyValue>
        {
            TaggedPropertyValue.Long(tags.StateFlags, 0x1),
            TaggedPropertyValue.Long(tags.ResponseStatus, 1),
            TaggedPropertyValue.Binary(tags.GlobalObjectId, globalObjectId),
            TaggedPropertyValue.Binary(tags.CleanGlobalObjectId, globalObjectId),
            TaggedPropertyValue.SysTime(tags.OwnerCriticalChange, nowUtc),
            TaggedPropertyValue.SysTime(tags.AttendeeCriticalChange, nowUtc),
            TaggedPropertyValue.Long(tags.AppointmentSequence, sequence),
            TaggedPropertyValue.Boolean(PropertyTags.ResponseRequested, true),
        };
        if (tags.FInvited != 0) values.Add(TaggedPropertyValue.Boolean(tags.FInvited, true));
        if (tags.AppointmentDuration != 0) values.Add(TaggedPropertyValue.Long(tags.AppointmentDuration, durationMinutes));
        return values;
    }

    /// <summary>
    /// The meeting request (MS-OXOCAL 2.2.6) sent to the attendees: the appointment's when/where under
    /// the shared global object id, busy status tentative on the attendee's side with the intended one
    /// beside it, and a response requested.
    /// </summary>
    public static MapiOutgoingMessage BuildMeetingRequest(MapiCalendarTags tags, AppointmentWrite appointment, byte[] globalObjectId, uint sequence, DateTime nowUtc)
    {
        var message = new MapiOutgoingMessage
        {
            MessageClass = PropertyTags.MeetingRequestClass,
            Subject = appointment.Subject ?? string.Empty,
        };
        var (text, html) = NormalizeBody(appointment.Body);
        if (html is not null) message.HtmlBody = html;
        else if (text is not null) message.TextBody = text;
        foreach (var r in MeetingRecipients(appointment, includeOrganizer: false))
            message.Recipients.Add(r with { Flags = null, TrackStatus = null });

        var extra = message.ExtraProperties;
        extra.Add(TaggedPropertyValue.Binary(tags.GlobalObjectId, globalObjectId));
        extra.Add(TaggedPropertyValue.Binary(tags.CleanGlobalObjectId, globalObjectId));
        extra.Add(TaggedPropertyValue.SysTime(tags.OwnerCriticalChange, nowUtc));
        extra.Add(TaggedPropertyValue.SysTime(tags.AttendeeCriticalChange, nowUtc));
        extra.Add(TaggedPropertyValue.Long(tags.AppointmentSequence, sequence));
        extra.Add(TaggedPropertyValue.SysTime(tags.StartWhole, appointment.StartUtc));
        extra.Add(TaggedPropertyValue.SysTime(tags.EndWhole, appointment.EndUtc));
        extra.Add(TaggedPropertyValue.Boolean(tags.AllDay, appointment.AllDay));
        extra.Add(TaggedPropertyValue.Long(tags.StateFlags, 0x1));
        extra.Add(TaggedPropertyValue.Long(tags.BusyStatus, 1));
        extra.Add(TaggedPropertyValue.Long(tags.IntendedBusyStatus, appointment.BusyStatus));
        extra.Add(TaggedPropertyValue.Boolean(PropertyTags.ResponseRequested, true));
        extra.Add(TaggedPropertyValue.Long(PropertyTags.Sensitivity, appointment.Sensitivity));
        extra.Add(TaggedPropertyValue.Boolean(tags.ReminderSet, appointment.ReminderMinutes is not null));
        if (appointment.ReminderMinutes is { } minutes) extra.Add(TaggedPropertyValue.Long(tags.ReminderDelta, (uint)Math.Max(0, minutes)));
        if (tags.AppointmentDuration != 0) extra.Add(TaggedPropertyValue.Long(tags.AppointmentDuration, appointment.DurationMinutes));
        if (!string.IsNullOrEmpty(appointment.Location)) { extra.Add(TaggedPropertyValue.Unicode(tags.Location, appointment.Location)); extra.Add(TaggedPropertyValue.Unicode(tags.Where, appointment.Location)); }
        extra.AddRange(TimeZoneProperties(tags, appointment));
        extra.AddRange(RecurrenceProperties(tags, appointment));
        if (appointment.IsRecurring) extra.Add(TaggedPropertyValue.Boolean(tags.Recurring, true));
        return message;
    }

    /// <summary>The cancellation (MS-OXOCAL 2.2.9) sent to the attendees when the organizer deletes the meeting.</summary>
    public static MapiOutgoingMessage BuildMeetingCancellation(MapiCalendarTags tags, MeetingIdentity meeting, uint sequence, DateTime nowUtc)
    {
        var message = new MapiOutgoingMessage
        {
            MessageClass = PropertyTags.MeetingCanceledClass,
            Subject = "Canceled: " + (meeting.Subject ?? string.Empty),
        };
        foreach (var r in meeting.Attendees ?? [])
        {
            if (r.IsOrganizer || string.IsNullOrEmpty(r.SmtpAddress)) continue;
            message.Recipients.Add(new RopMessageWrite.Recipient(r.IsOptional ? RopMessageWrite.RecipientType.Cc : RopMessageWrite.RecipientType.To, r.Name ?? r.SmtpAddress, r.SmtpAddress));
        }

        var extra = message.ExtraProperties;
        if (meeting.GlobalObjectId is { Length: > 0 } goid) extra.Add(TaggedPropertyValue.Binary(tags.GlobalObjectId, goid));
        if (meeting.CleanGlobalObjectId is { Length: > 0 } clean) extra.Add(TaggedPropertyValue.Binary(tags.CleanGlobalObjectId, clean));
        extra.Add(TaggedPropertyValue.SysTime(tags.OwnerCriticalChange, nowUtc));
        extra.Add(TaggedPropertyValue.SysTime(tags.AttendeeCriticalChange, nowUtc));
        extra.Add(TaggedPropertyValue.Long(tags.AppointmentSequence, sequence));
        extra.Add(TaggedPropertyValue.Long(tags.StateFlags, 0x5));
        extra.Add(TaggedPropertyValue.Long(tags.BusyStatus, 0));
        if (meeting.StartUtc is { } start) extra.Add(TaggedPropertyValue.SysTime(tags.StartWhole, start));
        if (meeting.EndUtc is { } end) extra.Add(TaggedPropertyValue.SysTime(tags.EndWhole, end));
        extra.Add(TaggedPropertyValue.Boolean(tags.AllDay, meeting.AllDay));
        if (!string.IsNullOrEmpty(meeting.Location)) { extra.Add(TaggedPropertyValue.Unicode(tags.Location, meeting.Location)); extra.Add(TaggedPropertyValue.Unicode(tags.Where, meeting.Location)); }
        return message;
    }

    /// <summary>
    /// Creates the organizer's meeting in the calendar (appointment + recipient table + organizer
    /// stamps) and sends the request to the attendees through the Outbox. Returns the appointment's id.
    /// </summary>
    public static async Task<ulong> CreateMeetingAsync(MapiSession session, ulong calendarFolderId, ulong outboxFolderId, ulong sentItemsFolderId,
        MapiCalendarTags tags, AppointmentWrite appointment, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var now = DateTime.UtcNow;
        var goid = GlobalObjectId.Create(now);

        var values = Properties(tags, appointment, includeClass: true);
        values.RemoveAll(v => v.Tag == tags.StateFlags || v.Tag == tags.ResponseStatus);
        values.AddRange(OrganizerProperties(tags, goid, 0, now, appointment.DurationMinutes));

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, calendarFolderId), handles, cancellationToken).ConfigureAwait(false);
        var createdId = RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        ulong id;
        try
        {
            var savedId = await SetAndSaveAsync(session, handles, values, MeetingRecipients(appointment, includeOrganizer: true), cancellationToken).ConfigureAwait(false);
            id = savedId != 0 ? savedId : createdId ?? 0;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        await MapiMessageComposer.SendAsync(session, outboxFolderId, sentItemsFolderId, BuildMeetingRequest(tags, appointment, goid, 0, now), cancellationToken, diagnostics).ConfigureAwait(false);
        diagnostics?.Invoke($"meeting 0x{id:X16}: request sent to {appointment.Attendees.Count} attendees");
        return id;
    }

    /// <summary>
    /// Rewrites the organizer's meeting with a bumped sequence and sends the updated request. An
    /// appointment that had no attendees before becomes a meeting here (fresh global object id).
    /// </summary>
    public static async Task UpdateMeetingAsync(MapiSession session, ulong calendarFolderId, ulong messageId, ulong outboxFolderId, ulong sentItemsFolderId,
        MapiCalendarTags tags, AppointmentWrite appointment, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var existing = await ReadMeetingIdentityAsync(session, calendarFolderId, messageId, tags, cancellationToken, diagnostics).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var wasMeeting = GlobalObjectId.IsWellFormed(existing.GlobalObjectId);
        var goid = wasMeeting ? existing.GlobalObjectId! : GlobalObjectId.Create(now);
        var sequence = wasMeeting ? existing.Sequence + 1 : 0;

        var values = Properties(tags, appointment, includeClass: false);
        values.AddRange(OrganizerProperties(tags, goid, sequence, now, appointment.DurationMinutes));

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(calendarFolderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            await SetAndSaveAsync(session, handles, values, MeetingRecipients(appointment, includeOrganizer: true), cancellationToken, clearRecipientsFirst: true, diagnostics).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        await MapiMessageComposer.SendAsync(session, outboxFolderId, sentItemsFolderId, BuildMeetingRequest(tags, appointment, goid, sequence, now), cancellationToken, diagnostics).ConfigureAwait(false);
        diagnostics?.Invoke($"meeting 0x{messageId:X16}: update (sequence {sequence}) sent to {appointment.Attendees.Count} attendees");
    }

    /// <summary>
    /// Deletes a meeting the account organizes: sends the cancellation to the attendees, marks the
    /// appointment cancelled, and moves it to Deleted Items. Returns false (having only moved the item)
    /// when there is nobody to tell: no attendees, not organized here, or no meeting identity.
    /// </summary>
    public static async Task<bool> CancelMeetingAsync(MapiSession session, ulong calendarFolderId, ulong messageId, ulong outboxFolderId, ulong sentItemsFolderId, ulong deletedItemsFolderId,
        MapiCalendarTags tags, string organizerAddress, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var meeting = await ReadMeetingIdentityAsync(session, calendarFolderId, messageId, tags, cancellationToken, diagnostics).ConfigureAwait(false);
        var attendees = (meeting.Attendees ?? []).Where(r => !r.IsOrganizer && !string.IsNullOrEmpty(r.SmtpAddress) && !string.Equals(r.SmtpAddress, organizerAddress, StringComparison.OrdinalIgnoreCase)).ToList();
        var organizedHere = string.IsNullOrEmpty(meeting.OrganizerAddress) || string.Equals(meeting.OrganizerAddress, organizerAddress, StringComparison.OrdinalIgnoreCase);

        if (attendees.Count == 0 || !organizedHere || !GlobalObjectId.IsWellFormed(meeting.GlobalObjectId))
        {
            await MapiMessageOperations.MoveAsync(session, calendarFolderId, deletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        var now = DateTime.UtcNow;
        var sequence = meeting.Sequence + 1;

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(calendarFolderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            var stamps = new List<TaggedPropertyValue>
            {
                TaggedPropertyValue.Long(tags.StateFlags, 0x5),
                TaggedPropertyValue.Long(tags.AppointmentSequence, sequence),
                TaggedPropertyValue.SysTime(tags.OwnerCriticalChange, now),
            };
            await SetAndSaveAsync(session, handles, stamps, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        await MapiMessageComposer.SendAsync(session, outboxFolderId, sentItemsFolderId, BuildMeetingCancellation(tags, meeting, sequence, now), cancellationToken, diagnostics).ConfigureAwait(false);
        diagnostics?.Invoke($"meeting 0x{messageId:X16}: cancellation (sequence {sequence}) sent to {attendees.Count} attendees");
        await MapiMessageOperations.MoveAsync(session, calendarFolderId, deletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The appointment properties (MS-OXOCAL 2.2.1): times in UTC, busy status, sensitivity, reminder, and the client tracking id.</summary>
    public static List<TaggedPropertyValue> Properties(MapiCalendarTags tags, AppointmentWrite appointment, bool includeClass)
    {
        var values = new List<TaggedPropertyValue>();
        if (includeClass)
        {
            values.Add(TaggedPropertyValue.Unicode(PropertyTags.MessageClass, PropertyTags.AppointmentMessageClass));
            values.Add(TaggedPropertyValue.Long(tags.StateFlags, 0));
            values.Add(TaggedPropertyValue.Long(tags.ResponseStatus, 0));
        }
        values.Add(TaggedPropertyValue.Boolean(tags.Recurring, appointment.IsRecurring));

        values.Add(TaggedPropertyValue.Unicode(PropertyTags.Subject, appointment.Subject ?? string.Empty));
        values.AddRange(BodyProperties(appointment.Body));
        values.Add(TaggedPropertyValue.Unicode(tags.Location, appointment.Location ?? string.Empty));
        values.Add(TaggedPropertyValue.SysTime(tags.StartWhole, appointment.StartUtc));
        values.Add(TaggedPropertyValue.SysTime(tags.EndWhole, appointment.EndUtc));
        values.Add(TaggedPropertyValue.Boolean(tags.AllDay, appointment.AllDay));
        values.Add(TaggedPropertyValue.Long(tags.BusyStatus, appointment.BusyStatus));
        values.Add(TaggedPropertyValue.Long(PropertyTags.Sensitivity, appointment.Sensitivity));
        values.Add(TaggedPropertyValue.Boolean(tags.ReminderSet, appointment.ReminderMinutes is not null));
        if (appointment.ReminderMinutes is { } minutes)
            values.Add(TaggedPropertyValue.Long(tags.ReminderDelta, (uint)Math.Max(0, minutes)));
        if (!string.IsNullOrEmpty(appointment.ClientTrackingId) && tags.ClientTrackingId != 0)
            values.Add(TaggedPropertyValue.Unicode(tags.ClientTrackingId, appointment.ClientTrackingId));

        values.AddRange(TimeZoneProperties(tags, appointment));
        values.AddRange(RecurrenceProperties(tags, appointment));
        return values;
    }

    /// <summary>
    /// What a series master carries beyond a single appointment (MS-OXOCAL 2.2.1.44 to 2.2.1.46):
    /// the recurrence blob built from the rule in the appointment's zone, the recurrence type, and
    /// the clip dates (UTC midnight of the first and last occurrence). Empty for a single appointment.
    /// </summary>
    public static List<TaggedPropertyValue> RecurrenceProperties(MapiCalendarTags tags, AppointmentWrite appointment)
    {
        var values = new List<TaggedPropertyValue>();
        if (!appointment.IsRecurring)
            return values;

        var pattern = BuildRecurrence(appointment);
        values.Add(TaggedPropertyValue.Binary(tags.AppointmentRecur, RecurrenceEncoder.Encode(pattern)));
        if (tags.RecurrenceType != 0)
            values.Add(TaggedPropertyValue.Long(tags.RecurrenceType, pattern.RecurFrequency switch
            {
                AppointmentRecurrence.FrequencyDaily => 1u,
                AppointmentRecurrence.FrequencyWeekly => 2u,
                AppointmentRecurrence.FrequencyMonthly => 3u,
                _ => 4u,
            }));
        if (tags.ClipStart != 0)
            values.Add(TaggedPropertyValue.SysTime(tags.ClipStart, DateTime.SpecifyKind(pattern.StartDate, DateTimeKind.Utc)));
        if (tags.ClipEnd != 0)
            values.Add(TaggedPropertyValue.SysTime(tags.ClipEnd, DateTime.SpecifyKind(pattern.EndDate ?? AppointmentRecurrence.FromMinutes(RecurrenceEncoder.NoEndDateMinutes), DateTimeKind.Utc)));
        return values;
    }

    /// <summary>The pattern for the rule, anchored on the first occurrence's wall clock in the appointment's zone.</summary>
    public static AppointmentRecurrence BuildRecurrence(AppointmentWrite appointment)
    {
        var zone = TimeZoneDefinition.Resolve(appointment.TimeZoneId);
        var startWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(appointment.StartUtc, DateTimeKind.Utc), zone);
        var duration = appointment.AllDay ? TimeSpan.FromDays(Math.Max(1, Math.Round((appointment.EndUtc - appointment.StartUtc).TotalDays))) : appointment.EndUtc - appointment.StartUtc;
        if (appointment.AllDay) startWall = startWall.Date;
        var pattern = RecurrenceEncoder.FromRRule(appointment.RecurrenceRule!, startWall, duration);

        if (appointment.ExistingRecurrence is { } existing && SamePattern(pattern, existing))
            pattern = pattern with { DeletedInstanceDates = existing.DeletedInstanceDates, ModifiedInstanceDates = existing.ModifiedInstanceDates, Exceptions = existing.Exceptions };

        return pattern;
    }

    /// <summary>Whether two patterns place occurrences on the same days; times may differ (a series moved to another hour keeps its exceptions).</summary>
    public static bool SamePattern(AppointmentRecurrence a, AppointmentRecurrence b)
        => a.RecurFrequency == b.RecurFrequency && a.PatternType == b.PatternType && a.Period == b.Period
        && a.DayOfWeekMask == b.DayOfWeekMask && a.Day == b.Day && a.Nth == b.Nth
        && a.EndType == b.EndType && a.StartDate == b.StartDate
        && (a.EndType != AppointmentRecurrence.EndAfterCount || a.OccurrenceCount == b.OccurrenceCount)
        && (a.EndType != AppointmentRecurrence.EndAfterDate || a.EndDate == b.EndDate);

    // ---- One occurrence of a meeting the account organizes (MS-OXOCAL 3.1.4.4.3 / 3.1.4.6) -----------

    /// <summary>
    /// The request for one changed occurrence: the master's identity with the instance date stamped
    /// into the global object id, the clean id unchanged, IsException and the original start, sent
    /// to the master's attendees.
    /// </summary>
    public static MapiOutgoingMessage BuildOccurrenceRequest(MapiCalendarTags tags, MeetingIdentity master, AppointmentWrite change, DateTime originalStartUtc, DateTime originalWall, uint sequence, DateTime nowUtc)
    {
        var instance = new AppointmentWrite
        {
            Subject = change.Subject ?? master.Subject, Body = change.Body, Location = change.Location,
            StartUtc = change.StartUtc, EndUtc = change.EndUtc, AllDay = change.AllDay, BusyStatus = change.BusyStatus,
            Sensitivity = change.Sensitivity, ReminderMinutes = change.ReminderMinutes, TimeZoneId = change.TimeZoneId,
            OrganizerAddress = master.OrganizerAddress, OrganizerName = master.OrganizerName,
        };
        foreach (var r in master.Attendees ?? [])
        {
            if (r.IsOrganizer || string.IsNullOrEmpty(r.SmtpAddress)) continue;
            instance.Attendees.Add(new MeetingAttendee(r.Name, r.SmtpAddress, r.IsOptional));
        }

        var message = BuildMeetingRequest(tags, instance, GlobalObjectId.ForInstance(master.GlobalObjectId!, originalWall.Date), sequence, nowUtc);
        StampInstance(tags, message, master, originalStartUtc);
        return message;
    }

    /// <summary>The cancellation for one removed occurrence, named by the instance id.</summary>
    public static MapiOutgoingMessage BuildOccurrenceCancellation(MapiCalendarTags tags, MeetingIdentity master, DateTime originalStartUtc, DateTime originalWall, uint sequence, DateTime nowUtc)
    {
        var instanceIdentity = master with { GlobalObjectId = GlobalObjectId.ForInstance(master.GlobalObjectId!, originalWall.Date), StartUtc = originalStartUtc, EndUtc = originalStartUtc + ((master.EndUtc ?? originalStartUtc) - (master.StartUtc ?? originalStartUtc)) };
        var message = BuildMeetingCancellation(tags, instanceIdentity, sequence, nowUtc);
        StampInstance(tags, message, master, originalStartUtc);
        return message;
    }

    /// <summary>Marks a meeting message as being about one instance: master's clean id, IsRecurring, IsException, the original start.</summary>
    private static void StampInstance(MapiCalendarTags tags, MapiOutgoingMessage message, MeetingIdentity master, DateTime originalStartUtc)
    {
        var extra = message.ExtraProperties;
        extra.RemoveAll(v => v.Tag == tags.CleanGlobalObjectId);
        if (master.CleanGlobalObjectId is { Length: > 0 } clean) extra.Add(TaggedPropertyValue.Binary(tags.CleanGlobalObjectId, clean));
        if (tags.IsRecurring != 0) extra.Add(TaggedPropertyValue.Boolean(tags.IsRecurring, true));
        if (tags.IsException != 0) extra.Add(TaggedPropertyValue.Boolean(tags.IsException, true));
        if (tags.ExceptionReplaceTime != 0) extra.Add(TaggedPropertyValue.SysTime(tags.ExceptionReplaceTime, DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc)));
    }

    /// <summary>Inline this many HTML bytes at most in RopSetProperties; larger notes fall back to their text.</summary>
    private const int InlineHtmlLimit = 4 * 1024;

    /// <summary>
    /// The body as the store should hold it. HTML notes go into PidTagHtml (the store derives the
    /// plain body from it, as it does for mail); plain notes into PidTagBody; an editor's empty
    /// shell (a lone div/br) is written as nothing at all so OWA shows a blank Notes field.
    /// </summary>
    public static List<TaggedPropertyValue> BodyProperties(string? body)
    {
        var (text, html) = NormalizeBody(body);
        if (html is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            if (bytes.Length <= InlineHtmlLimit)
                return [TaggedPropertyValue.Binary(PropertyTags.Html, bytes)];
        }

        return [TaggedPropertyValue.Unicode(PropertyTags.Body, text ?? string.Empty)];
    }

    /// <summary>
    /// Splits a description into (text, html): html is set only when the input is markup with some
    /// visible content; text is the markup stripped of tags and entities, or the input itself when
    /// it was plain. Both null for an empty or empty-looking description.
    /// </summary>
    public static (string? Text, string? Html) NormalizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        if (!body.Contains('<'))
            return (body, null);

        var stripped = Regex.Replace(body, "<[^>]*>", " ");
        stripped = WebUtility.HtmlDecode(stripped).Replace('\u00A0', ' ');
        stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
        return stripped.Length == 0 ? (null, null) : (stripped, body);
    }

    /// <summary>
    /// The zone blobs (MS-OXOCAL 2.2.1.39 to 2.2.1.43): the display definitions for start and end, the
    /// recurrence definition and struct that a series needs, and the zone's display name. Only the
    /// tags the store resolved are written; none when the zone cannot be found.
    /// </summary>
    public static List<TaggedPropertyValue> TimeZoneProperties(MapiCalendarTags tags, AppointmentWrite appointment)
    {
        var values = new List<TaggedPropertyValue>();
        var zone = TimeZoneDefinition.Resolve(appointment.TimeZoneId);
        var definition = TimeZoneDefinition.Encode(zone, appointment.StartUtc);

        if (tags.TimeZoneDefinitionStart != 0) values.Add(TaggedPropertyValue.Binary(tags.TimeZoneDefinitionStart, definition));
        if (tags.TimeZoneDefinitionEnd != 0) values.Add(TaggedPropertyValue.Binary(tags.TimeZoneDefinitionEnd, definition));
        if (tags.TimeZoneDefinitionRecur != 0) values.Add(TaggedPropertyValue.Binary(tags.TimeZoneDefinitionRecur, definition));
        if (tags.TimeZoneStruct != 0) values.Add(TaggedPropertyValue.Binary(tags.TimeZoneStruct, TimeZoneDefinition.EncodeStruct(zone, appointment.StartUtc)));
        if (tags.TimeZoneDescription != 0) values.Add(TaggedPropertyValue.Unicode(tags.TimeZoneDescription, zone.DisplayName));
        return values;
    }

    /// <summary>
    /// The Windows time zone key of a TZDEFINITION (MS-OXOCAL 2.2.1.41): MajorVersion, MinorVersion,
    /// cbHeader, Reserved, cchKeyName, KeyName (UTF-16), then the rules this client does not need.
    /// </summary>
    public static string? TimeZoneKeyName(byte[]? definition)
    {
        if (definition is not { Length: > 8 })
            return null;

        try
        {
            var r = new RopReader(definition);
            r.UInt8(); r.UInt8(); r.UInt16(); r.UInt16();
            var chars = r.UInt16();
            if (chars == 0 || chars * 2 > r.Remaining)
                return null;
            return Encoding.Unicode.GetString(r.Bytes(chars * 2).Span);
        }
        catch (MapiFormatException)
        {
            return null;
        }
    }
}
