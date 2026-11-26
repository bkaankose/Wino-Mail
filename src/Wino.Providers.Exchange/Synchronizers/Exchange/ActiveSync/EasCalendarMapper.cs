using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>An occurrence of a series as stored, with the item data it was read from (the master's, or the exception's merged onto it).</summary>
internal sealed record EasOccurrence(CalendarItem Item, XElement Data);

internal static partial class EasDataMapper
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Separates a series' server id from an occurrence's original start (UTC) in the occurrence's remote id.</summary>
    internal const char OccurrenceSeparator = '#';

    internal static readonly TimeSpan CalendarWindowPast = TimeSpan.FromDays(92);
    internal static readonly TimeSpan CalendarWindowFuture = TimeSpan.FromDays(366);

    internal static Guid? ClientEventId(XElement data)
        => Guid.TryParse(Value(data, Calendar, "UID"), out var id) ? id : null;

    internal static string Timestamp(DateTime utc) => utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>The series server id and the occurrence key of an occurrence's remote id, or null for a single event or a series.</summary>
    internal static (string SeriesId, string InstanceKey)? SplitOccurrenceId(string? remoteEventId)
    {
        var id = Wino.Core.Domain.Extensions.CalendarRemoteEventIdExtensions.GetProviderRemoteEventId(remoteEventId ?? string.Empty);
        var separator = id.LastIndexOf(OccurrenceSeparator);
        return separator > 0 && separator < id.Length - 1 ? (id[..separator], id[(separator + 1)..]) : null;
    }

    /// <summary>
    /// Reads a single event or a series master. Returns null, and logs, for an item without a usable start; every
    /// other defect degrades: an unknown zone reads as UTC, an unreadable recurrence as a single event.
    /// </summary>
    internal static CalendarItem? ReadEvent(XElement data, AccountCalendar calendar, string remoteId, string? accountAddress)
    {
        if (TryDate(Value(data, Calendar, "StartTime")) is not { } start)
        {
            Serilog.Log.Warning("EAS event {RemoteId} has no readable start time; it is skipped.", remoteId);
            return null;
        }
        var end = TryDate(Value(data, Calendar, "EndTime")) ?? start;
        if (end < start)
            end = start;

        var eventZone = EasTimeZone.Read(data, start.Year);
        var zone = eventZone.Stored;
        var allDay = Value(data, Calendar, "AllDayEvent") == "1";
        DateTime localStart, localEnd;
        if (allDay)
        {
            localStart = AllDayDate(start, eventZone.Zone);
            localEnd = AllDayDate(end, eventZone.Zone);
            if (localEnd <= localStart)
                localEnd = localStart.AddDays(1);
        }
        else
        {
            localStart = TimeZoneInfo.ConvertTimeFromUtc(start, zone);
            localEnd = TimeZoneInfo.ConvertTimeFromUtc(end, zone);
        }

        string? recurrence = null;
        if (data.Element(Calendar + "Recurrence") is { HasElements: true } recurrenceElement)
        {
            try { recurrence = ReadRecurrence(recurrenceElement); }
            catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException or System.IO.InvalidDataException)
            {
                Serilog.Log.Warning(exception, "EAS event {RemoteId} has a recurrence Wino cannot read; it is shown as a single event.", remoteId);
            }
        }

        var organizer = Value(data, Calendar, "OrganizerEmail");
        var status = ReadStatus(data);
        var zoneId = EasTimeZone.IanaId(zone);
        return new CalendarItem
        {
            Id = StableId(Key(calendar.AccountId, ServerIdOf(calendar.RemoteCalendarId), remoteId)), CalendarId = calendar.Id, AssignedCalendar = calendar,
            RemoteEventId = remoteId, Title = Value(data, Calendar, "Subject"), Description = SafeBodyText(data),
            Location = ReadLocation(data), StartDate = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified),
            DurationInSeconds = (localEnd - localStart).TotalSeconds, StartTimeZone = zoneId, EndTimeZone = zoneId,
            OrganizerEmail = organizer, OrganizerDisplayName = Value(data, Calendar, "OrganizerName"),
            IsLocked = !string.IsNullOrEmpty(organizer) && !string.IsNullOrEmpty(accountAddress) && !string.Equals(organizer, accountAddress, StringComparison.OrdinalIgnoreCase),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = TryDate(Value(data, Calendar, "DtStamp")) ?? DateTime.UtcNow,
            Recurrence = recurrence, Status = status, IsHidden = status == CalendarItemStatus.Cancelled,
            Visibility = Value(data, Calendar, "Sensitivity") switch { "1" or "2" => CalendarItemVisibility.Private, "3" => CalendarItemVisibility.Confidential, _ => CalendarItemVisibility.Public },
            ShowAs = Value(data, Calendar, "BusyStatus") switch { "0" => CalendarItemShowAs.Free, "1" => CalendarItemShowAs.Tentative,
                "3" => CalendarItemShowAs.OutOfOffice, "4" => CalendarItemShowAs.WorkingElsewhere, _ => CalendarItemShowAs.Busy }
        };
    }

    /// <summary>
    /// The day of an all-day bound. 16.x sends floating midnight as UTC; 14.x sends local midnight of the event's zone
    /// in UTC. A value that is neither is rounded to the nearest day rather than shifted.
    /// </summary>
    private static DateTime AllDayDate(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        if (local.TimeOfDay == TimeSpan.Zero)
            return local.Date;
        return utc.TimeOfDay == TimeSpan.Zero ? utc.Date : local.AddHours(12).Date;
    }

    /// <summary>
    /// The account's own state: MeetingStatus bit 4 marks a cancelled meeting, ResponseType the answer
    /// (2 tentative, 3 accepted, 4 declined, 5 not responded). A declined meeting is hidden, as on the other Exchange transports.
    /// </summary>
    private static CalendarItemStatus ReadStatus(XElement data)
    {
        if (int.TryParse(Value(data, Calendar, "MeetingStatus"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meeting) && (meeting & 4) != 0)
            return CalendarItemStatus.Cancelled;
        return Value(data, Calendar, "ResponseType") switch
        {
            "2" => CalendarItemStatus.Tentative,
            "4" => CalendarItemStatus.Cancelled,
            "5" => CalendarItemStatus.NotResponded,
            _ => CalendarItemStatus.Accepted
        };
    }

    /// <summary>One attendee per address; a server that lists an address twice must not collide on the row id.</summary>
    internal static List<CalendarEventAttendee> ReadAttendees(XElement data, Guid eventId)
        => data.Element(Calendar + "Attendees")?.Elements(Calendar + "Attendee")
            .Where(attendee => !string.IsNullOrWhiteSpace(Value(attendee, Calendar, "Email")))
            .GroupBy(attendee => Value(attendee, Calendar, "Email").Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(attendee => new CalendarEventAttendee
            {
                Id = StableId(eventId.ToString("N") + Value(attendee, Calendar, "Email").Trim().ToLowerInvariant()), CalendarItemId = eventId,
                Email = Value(attendee, Calendar, "Email").Trim(), Name = Value(attendee, Calendar, "Name"),
                IsOptionalAttendee = Value(attendee, Calendar, "AttendeeType") == "2",
                AttendenceStatus = Value(attendee, Calendar, "AttendeeStatus") switch
                { "2" => AttendeeStatus.Tentative, "3" => AttendeeStatus.Accepted, "4" => AttendeeStatus.Declined, _ => AttendeeStatus.NeedsAction }
            }).ToList() ?? [];

    /// <summary>
    /// The file attachments of an event or exception (MS-ASAIRS 2.2.2.8, on calendar items in 16.x per MS-ASCAL): by
    /// value (Method 1) and not inline, keyed by FileReference. An item without the element has none.
    /// </summary>
    internal static List<SyncedCalendarAttachment> ReadAttachments(XElement data)
        => data.Element(Body + "Attachments")?.Elements(Body + "Attachment")
            .Where(attachment => Value(attachment, Body, "Method") == "1" && Value(attachment, Body, "IsInline") != "1" && Value(attachment, Body, "FileReference").Length > 0)
            .Select(attachment => new SyncedCalendarAttachment
            {
                RemoteId = Value(attachment, Body, "FileReference"),
                FileName = Value(attachment, Body, "DisplayName") is { Length: > 0 } name ? name : "attachment",
                Size = long.TryParse(Value(attachment, Body, "EstimatedDataSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0
            }).ToList() ?? [];

    /// <summary>
    /// The attachment changes of a 16.x Sync Add or Change (MS-ASAIRS 2.2.2.2 Add, 2.2.2.21 Delete). Each added file's
    /// ClientId is its draft id, so a Sync sent again names the same attachments.
    /// </summary>
    internal static XElement AttachmentChanges(IEnumerable<string> deletedFileReferences, IEnumerable<ExchangeAttachmentContent> added)
        => new(Body + "Attachments",
            deletedFileReferences.Select(reference => new XElement(Body + "Delete", new XElement(Body + "FileReference", reference))),
            added.Select(file => new XElement(Body + "Add",
                new XElement(Body + "ClientId", file.DraftId.ToString("N")),
                EasDraftMapper.Opaque(Body + "Content", file.Content),
                new XElement(Body + "DisplayName", EasWbxml.XmlSafe(file.FileName)),
                new XElement(Body + "Method", "1"),
                new XElement(Body + "ContentType", file.ContentType))));

    internal static int? ReadReminderMinutes(XElement data)
        => int.TryParse(Value(data, Calendar, "Reminder"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0 && minutes <= 60 * 24 * 365
            ? minutes : null;

    /// <summary>
    /// The occurrences of a series whose start falls in the window, each keyed by its original start in UTC (the
    /// value 14.x calls ExceptionStartTime and 16.x InstanceId). The rule is evaluated in the event's wall clock and
    /// converted with its zone, so occurrences keep their local time across daylight saving changes. Deleted
    /// exceptions are left out; changed ones carry the exception's fields merged onto the master's.
    /// </summary>
    internal static List<EasOccurrence> ExpandEvent(XElement data, CalendarItem master, string? accountAddress, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var result = new List<EasOccurrence>();
        if (string.IsNullOrEmpty(master.Recurrence) || master.AssignedCalendar is not AccountCalendar calendar)
            return result;

        // The rule runs in the event's own zone; rows store their times in the system zone they name.
        var stored = EasTimeZone.Resolve(master.StartTimeZone);
        var masterStartUtc = master.IsAllDayEvent ? DateTime.SpecifyKind(master.StartDate, DateTimeKind.Utc) : ToUtc(master.StartDate, stored);
        var zone = EasTimeZone.Read(data, masterStartUtc.Year).Zone;
        var masterStart = master.IsAllDayEvent ? master.StartDate : TimeZoneInfo.ConvertTimeFromUtc(masterStartUtc, zone);
        var exceptions = new Dictionary<DateTime, XElement>();
        foreach (var exception in data.Element(Calendar + "Exceptions")?.Elements(Calendar + "Exception") ?? Enumerable.Empty<XElement>())
        {
            if ((TryDate(Value(exception, Calendar, "ExceptionStartTime")) ?? TryDate(Value(exception, Body, "InstanceId"))) is { } original)
                exceptions[original] = exception;
        }

        var remoteId = master.RemoteEventId.GetProviderRemoteEventIdSafe();
        IEnumerable<DateTime> starts;
        try
        {
            var pattern = new RecurrencePattern(master.Recurrence["RRULE:".Length..]);
            // Floating evaluation needs a floating bound; UNTIL is a UTC instant on the wire.
            if (pattern.Until is { } until)
                pattern.Until = new CalDateTime(Floating(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(until.AsUtc, DateTimeKind.Utc), zone)), true);
            var series = new CalendarEvent
            {
                Start = new CalDateTime(DateTime.SpecifyKind(masterStart, DateTimeKind.Unspecified), true),
                End = new CalDateTime(DateTime.SpecifyKind(masterStart.AddSeconds(master.DurationInSeconds), DateTimeKind.Unspecified), true)
            };
            series.RecurrenceRule = pattern;
            var windowStartLocal = Floating(TimeZoneInfo.ConvertTimeFromUtc(windowStartUtc, zone)).AddDays(-1);
            var windowEndLocal = Floating(TimeZoneInfo.ConvertTimeFromUtc(windowEndUtc, zone)).AddDays(1);
            starts = series.GetOccurrences(new CalDateTime(windowStartLocal, true), new EvaluationOptions { MaxUnmatchedIncrementsLimit = 10_000 })
                .Select(occurrence => occurrence.Period.StartTime.Value)
                .TakeWhile(local => local < windowEndLocal)
                .Take(5000)
                .ToList();
        }
        // Only a rule the evaluator cannot expand within its limits falls back; any other failure is a defect and fails the item.
        catch (EvaluationException exception)
        {
            Serilog.Log.Warning(exception, "EAS series {RemoteId} could not be expanded; only its first occurrence is shown.", remoteId);
            starts = [masterStart];
        }

        foreach (var local in starts)
        {
            var originalUtc = ToUtc(local, zone);
            var key = Timestamp(originalUtc);
            var occurrenceData = data;
            CalendarItem? occurrence;
            if (exceptions.Remove(originalUtc, out var exception))
            {
                if (Value(exception, Calendar, "Deleted") == "1")
                    continue;
                occurrenceData = ExceptionData(data, exception, originalUtc, master.DurationInSeconds, master.IsAllDayEvent);
                occurrence = ReadEvent(occurrenceData, calendar, key, accountAddress);
            }
            else
            {
                occurrence = ReadEvent(data, calendar, key, accountAddress);
                if (occurrence != null)
                    occurrence.StartDate = DateTime.SpecifyKind(master.IsAllDayEvent ? local : TimeZoneInfo.ConvertTimeFromUtc(originalUtc, stored), DateTimeKind.Unspecified);
            }
            if (occurrence == null)
                continue;
            var startUtc = ToUtc(occurrence.StartDate, EasTimeZone.Resolve(occurrence.StartTimeZone));
            if (startUtc.AddSeconds(occurrence.DurationInSeconds) < windowStartUtc || startUtc > windowEndUtc)
                continue;

            AsOccurrence(occurrence, master, remoteId, key);
            result.Add(new EasOccurrence(occurrence, occurrenceData));
        }
        return result;
    }

    private static void AsOccurrence(CalendarItem occurrence, CalendarItem master, string seriesId, string key)
    {
        occurrence.Id = StableId(master.Id.ToString("N") + OccurrenceSeparator + key);
        occurrence.RemoteEventId = seriesId + OccurrenceSeparator + key;
        occurrence.Recurrence = null;
        occurrence.RecurringCalendarItemId = master.Id;
    }

    private static XElement ExceptionData(XElement master, XElement exception, DateTime originalUtc, double durationSeconds, bool allDay)
    {
        var merged = EasSynchronizationEngine.Merge(master.ToString(SaveOptions.DisableFormatting), exception);
        merged.Elements().Where(element => element.Name == Calendar + "Recurrence" || element.Name == Calendar + "Exceptions" ||
            element.Name == Calendar + "ExceptionStartTime" || element.Name == Calendar + "Deleted" || element.Name == Body + "InstanceId").Remove();
        if (exception.Element(Calendar + "StartTime") == null)
            merged.SetElementValue(Calendar + "StartTime", Timestamp(originalUtc));
        if (exception.Element(Calendar + "EndTime") == null)
        {
            var start = TryDate(Value(merged, Calendar, "StartTime")) ?? originalUtc;
            merged.SetElementValue(Calendar + "EndTime", Timestamp(start.AddSeconds(durationSeconds)));
        }
        if (exception.Element(Calendar + "AllDayEvent") == null)
            merged.SetElementValue(Calendar + "AllDayEvent", allDay ? "1" : "0");
        return merged;
    }

    // ConvertTimeFromUtc returns Kind=Local when the zone is the machine's own, which CalDateTime rejects.
    private static DateTime Floating(DateTime wallClock) => DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

    private static DateTime ToUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        // A wall-clock time skipped by a daylight saving change is moved past the gap rather than rejected.
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static string GetProviderRemoteEventIdSafe(this string? remoteEventId)
        => Wino.Core.Domain.Extensions.CalendarRemoteEventIdExtensions.GetProviderRemoteEventId(remoteEventId ?? string.Empty);

    // EAS 16.x moved the location to AirSyncBase:Location and rejects Calendar:Location.
    private static string ReadLocation(XElement data)
        => data.Element(Calendar + "Location")?.Value is { Length: > 0 } location ? location
            : data.Element(Body + "Location")?.Element(Body + "DisplayName")?.Value ?? string.Empty;

    private static readonly HashSet<string> ServerOnlyEventFields = new(StringComparer.Ordinal)
        { "ResponseRequested", "ResponseType", "AppointmentReplyTime" };

    // EAS 16.x: the server owns these; sending them back in a Change fails with status 6.
    private static readonly HashSet<string> ServerOnlyEventFields16 = new(StringComparer.Ordinal)
        { "UID", "DtStamp", "OrganizerName", "OrganizerEmail" };

    /// <summary>
    /// The stored server copy merged with the edit, reduced to what a Sync Change may carry. Measured against Exchange
    /// SE: read-only response fields, an empty reminder and the body size fail with status 6 on 14.1 or 16.1.
    /// </summary>
    internal static XElement ForEventChange(XElement merged, bool isProtocol16)
    {
        var result = new XElement(merged);
        StripServerOnly(result, isProtocol16);
        // 16.x rejects the exception list in a series Change (status 6) and keeps the exceptions it has; occurrences
        // are changed by InstanceId there.
        if (isProtocol16)
            result.Elements(Calendar + "Exceptions").Remove();
        if (isProtocol16 && result.Element(Calendar + "Location") is { } legacyLocation)
        {
            legacyLocation.Remove();
            result.Element(Body + "Location")?.Remove();
            result.Add(WriteLocation(legacyLocation.Value));
        }
        return result;
    }

    private static void StripServerOnly(XElement data, bool isProtocol16)
    {
        data.Elements().Where(element => element.Name.Namespace == Calendar && (ServerOnlyEventFields.Contains(element.Name.LocalName) ||
            (isProtocol16 && ServerOnlyEventFields16.Contains(element.Name.LocalName)) ||
            (element.Name.LocalName == "Reminder" && element.Value.Length == 0))).Remove();
        data.Elements(Body + "NativeBodyType").Remove();
        data.Element(Body + "Body")?.Elements().Where(element => element.Name.LocalName is "EstimatedDataSize" or "Truncated" or "Preview").Remove();
        foreach (var exception in data.Element(Calendar + "Exceptions")?.Elements(Calendar + "Exception") ?? Enumerable.Empty<XElement>())
        {
            StripServerOnly(exception, isProtocol16);
            exception.Elements(Calendar + "MeetingStatus").Remove();
            exception.Elements(Calendar + "DtStamp").Remove();
        }
    }

    private static XElement WriteLocation(string location)
        => new(Body + "Location", new XElement(Body + "DisplayName", location));

    /// <summary>
    /// The application data for an event. All-day events are floating midnights without a zone on 16.x, and local
    /// midnight of the event's zone (or the device's) with that zone on 14.x, as the server stores them.
    /// <paramref name="serverTimeZone"/>, the TimeZone the server holds for an event whose zone the edit keeps, is
    /// written back unchanged and the rule's days are computed in it, also when no system zone is equivalent to it.
    /// </summary>
    internal static XElement WriteEvent(CalendarItem item, IReadOnlyList<CalendarEventAttendee>? attendees, IReadOnlyList<Reminder> reminders, bool isProtocol16,
        string? serverTimeZone = null)
    {
        var stored = EasTimeZone.Resolve(item.StartTimeZone);
        var eventZone = string.IsNullOrEmpty(serverTimeZone) ? null : EasTimeZone.FromBlob(serverTimeZone);
        if (eventZone == null)
            serverTimeZone = null;
        TimeZoneInfo zone;
        DateTime start, end;
        if (item.IsAllDayEvent)
        {
            zone = isProtocol16 ? TimeZoneInfo.Utc : eventZone ?? (string.IsNullOrEmpty(item.StartTimeZone) ? TimeZoneInfo.Local : stored);
            start = ToUtc(item.StartDate.Date, zone);
            end = ToUtc(item.EndDate.Date > item.StartDate.Date ? item.EndDate.Date : item.StartDate.Date.AddDays(1), zone);
        }
        else
        {
            zone = eventZone ?? stored;
            start = ToUtc(item.StartDate, stored);
            end = ToUtc(item.EndDate, stored);
        }

        var result = new XElement(Sync + "ApplicationData",
            new XElement(Calendar + "Subject", item.Title ?? string.Empty),
            isProtocol16 ? WriteLocation(item.Location ?? string.Empty) : new XElement(Calendar + "Location", item.Location ?? string.Empty),
            new XElement(Calendar + "StartTime", Timestamp(start)),
            new XElement(Calendar + "EndTime", Timestamp(end)),
            isProtocol16 ? null : new XElement(Calendar + "DtStamp", Timestamp(DateTime.UtcNow)),
            new XElement(Calendar + "AllDayEvent", item.IsAllDayEvent ? "1" : "0"), new XElement(Calendar + "BusyStatus", (int)item.ShowAs),
            new XElement(Calendar + "Sensitivity", item.Visibility switch { CalendarItemVisibility.Private => "2", CalendarItemVisibility.Confidential => "3", _ => "0" }),
            item.IsAllDayEvent && isProtocol16 ? null : new XElement(Calendar + "TimeZone", serverTimeZone ?? EasTimeZone.Write(zone, start)),
            HtmlBody(item.Description));
        if (attendees != null)
            result.Add(new XElement(Calendar + "Attendees", attendees.Where(attendee => !attendee.IsOrganizer && !string.IsNullOrWhiteSpace(attendee.Email)).Select(attendee =>
                new XElement(Calendar + "Attendee", new XElement(Calendar + "Email", attendee.Email), new XElement(Calendar + "Name", attendee.Name ?? attendee.Email),
                    new XElement(Calendar + "AttendeeType", attendee.IsOptionalAttendee ? "2" : "1")))));
        if (reminders.Count > 0)
            result.Add(new XElement(Calendar + "Reminder", Math.Max(0, reminders.Min(reminder => reminder.DurationInSeconds) / 60)));

        if (!string.IsNullOrEmpty(item.Recurrence) && !item.IsRecurringChild)
            result.Add(WriteRecurrence(item, start, zone));
        return result;
    }

    private static XElement WriteRecurrence(CalendarItem item, DateTime startUtc, TimeZoneInfo zone)
    {
        var rule = item.Recurrence.Split(new[] { Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator, "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("RRULE:", StringComparison.Ordinal))
            ?? throw new NotSupportedException("The event recurrence is not an RRULE.");

        var pattern = new RecurrencePattern(rule[6..].Trim());
        var setPosition = pattern.BySetPosition.Count > 0 ? pattern.BySetPosition[0] : (int?)null;
        var relative = pattern.ByDay.Count > 0 && (setPosition != null || pattern.ByDay.Any(day => day.Offset is not null and not 0));
        // A daily rule limited to weekdays recurs on those days of every week: an EAS weekly pattern. Every n days on
        // some weekdays (n > 1) has no EAS form.
        if (pattern.Frequency == FrequencyType.Daily && pattern.ByDay.Count > 0 && Math.Max(1, pattern.Interval) > 1)
            throw new NotSupportedException("The event recurrence cannot be represented by EAS.");
        var type = pattern.Frequency switch
        {
            FrequencyType.Daily => pattern.ByDay.Count > 0 ? 1 : 0, FrequencyType.Weekly => 1,
            FrequencyType.Monthly => relative ? 3 : 2, FrequencyType.Yearly => relative ? 6 : 5,
            _ => throw new NotSupportedException("The event recurrence cannot be represented by EAS.")
        };
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
        var recurrence = new XElement(Calendar + "Recurrence", new XElement(Calendar + "Type", type),
            new XElement(Calendar + "Interval", Math.Max(1, pattern.Interval)));
        if (pattern.Count > 0)
            recurrence.Add(new XElement(Calendar + "Occurrences", pattern.Count));
        if (pattern.Until is { } until)
            recurrence.Add(new XElement(Calendar + "Until", Timestamp(item.IsAllDayEvent ? until.AsUtc.Date : until.AsUtc)));
        if (type is 1 or 3 or 6)
        {
            var days = pattern.ByDay.Count == 0 ? 1 << (int)localStart.DayOfWeek : pattern.ByDay.Aggregate(0, (mask, day) => mask | (1 << (int)day.DayOfWeek));
            recurrence.Add(new XElement(Calendar + "DayOfWeek", days));
        }
        if (type is 3 or 6)
        {
            var offset = setPosition ?? pattern.ByDay.First().Offset ?? 0;
            if (offset == 0 || offset < -1 || offset > 4 || (setPosition == null && pattern.ByDay.Select(day => day.Offset).Distinct().Count() > 1))
                throw new NotSupportedException("The event's ordinal weekday cannot be represented by EAS.");

            recurrence.Add(new XElement(Calendar + "WeekOfMonth", offset == -1 ? 5 : offset));
        }
        if (type is 2 or 5)
            recurrence.Add(new XElement(Calendar + "DayOfMonth", pattern.ByMonthDay.FirstOrDefault(localStart.Day)));
        if (type is 5 or 6)
            recurrence.Add(new XElement(Calendar + "MonthOfYear", pattern.ByMonth.FirstOrDefault(localStart.Month)));
        return recurrence;
    }

    /// <summary>
    /// The EAS rule as an RRULE. An ordinal (WeekOfMonth, 5 = last) applies to the set of days, so "the last day" or
    /// "the first weekday" of a month becomes BYDAY with BYSETPOS, not one ordinal per day. A daily pattern with
    /// DayOfWeek recurs weekly on those days, every Interval weeks.
    /// </summary>
    private static string ReadRecurrence(XElement recurrence)
    {
        var type = Value(recurrence, Calendar, "Type");
        var weeklyDaily = type == "0" && Value(recurrence, Calendar, "DayOfWeek").Length > 0;
        var frequency = type switch { "0" => weeklyDaily ? "WEEKLY" : "DAILY", "1" => "WEEKLY", "2" or "3" => "MONTHLY", "5" or "6" => "YEARLY",
            _ => throw new NotSupportedException("The EAS recurrence type is not supported.") };
        var parts = new List<string> { "FREQ=" + frequency };
        foreach (var (tag, key) in new[] { ("Interval", "INTERVAL"), ("Occurrences", "COUNT"), ("DayOfMonth", "BYMONTHDAY"), ("MonthOfYear", "BYMONTH") })
        {
            var value = Value(recurrence, Calendar, tag);
            if (string.IsNullOrEmpty(value))
                continue;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 1)
                throw new FormatException("The EAS recurrence " + tag + " is invalid.");
            parts.Add(key + "=" + number.ToString(CultureInfo.InvariantCulture));
        }
        if (Date(Value(recurrence, Calendar, "Until")) is { } until)
            parts.Add("UNTIL=" + Timestamp(until));

        if (int.TryParse(Value(recurrence, Calendar, "DayOfWeek"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask) && (mask & 127) != 0)
        {
            var days = new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }.Where((_, index) => (mask & (1 << index)) != 0).ToList();
            var week = Value(recurrence, Calendar, "WeekOfMonth");
            var ordinal = type is "3" or "6" ? week == "5" ? "-1" : week : string.Empty;
            if (ordinal.Length > 0 && ordinal is not ("1" or "2" or "3" or "4" or "-1"))
                throw new FormatException("The EAS recurrence WeekOfMonth is invalid.");
            if (ordinal.Length > 0 && days.Count > 1)
                parts.Add("BYDAY=" + string.Join(',', days) + ";BYSETPOS=" + ordinal);
            else
                parts.Add("BYDAY=" + string.Join(',', days.Select(day => ordinal + day)));
        }
        else if (type is "1" or "3" or "6" || weeklyDaily)
            throw new FormatException("The EAS recurrence has no days.");

        return "RRULE:" + string.Join(';', parts);
    }
}
