using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Services;

public static class CalDavIcsMutator
{
    private const string OccurrenceSeparator = "::";

    public static string Normalize(string icsContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icsContent);
        var calendar = IcalCalendar.Load(icsContent)
            ?? throw new InvalidOperationException("The generated CalDAV resource is not a valid iCalendar object.");
        return new CalendarSerializer(calendar).SerializeToString();
    }

    public static string UpdateEvent(
        string existingIcs,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee> attendees)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        ArgumentNullException.ThrowIfNull(item);

        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var (uid, occurrenceKey) = SplitRemoteEventId(item.RemoteEventId);
        var target = FindEvent(calendar, uid, occurrenceKey);

        if (target == null && !string.IsNullOrWhiteSpace(occurrenceKey))
        {
            var master = calendar.Events.FirstOrDefault(value =>
                string.Equals(value.Uid, uid, StringComparison.Ordinal) && value.RecurrenceIdentifier == null)
                ?? throw new InvalidOperationException("The recurring CalDAV master event is missing from the stored resource.");

            target = master.Copy<CalendarEvent>();
            target.Properties.Clear("RRULE");
            target.Properties.Clear("EXRULE");
            target.Properties.Clear("RDATE");
            target.Properties.Clear("EXDATE");
            target.RecurrenceIdentifier = new RecurrenceIdentifier(
                ParseOccurrenceKey(occurrenceKey, master.Start),
                RecurrenceRange.ThisInstance);
            calendar.Events.Add(target);
        }

        if (target == null)
            throw new InvalidOperationException("The CalDAV event is missing from the stored resource.");

        ApplyEditableProperties(target, item, attendees);
        return new CalendarSerializer(calendar).SerializeToString();
    }

    public static string RemoveOccurrence(string existingIcs, string remoteEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        var (uid, occurrenceKey) = SplitRemoteEventId(remoteEventId);
        if (string.IsNullOrWhiteSpace(occurrenceKey))
            throw new ArgumentException("A recurring occurrence ID is required.", nameof(remoteEventId));

        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var master = calendar.Events.FirstOrDefault(value =>
            string.Equals(value.Uid, uid, StringComparison.Ordinal) && value.RecurrenceIdentifier == null)
            ?? throw new InvalidOperationException("The recurring CalDAV master event is missing from the stored resource.");
        var recurrenceId = ParseOccurrenceKey(occurrenceKey, master.Start);
        var existingException = FindEvent(calendar, uid, occurrenceKey);

        if (existingException != null)
            calendar.Events.Remove(existingException);

        master.ExceptionDates.Add(recurrenceId);
        master.Sequence++;
        master.DtStamp = CalDateTime.UtcNow;
        master.LastModified = CalDateTime.UtcNow;

        return new CalendarSerializer(calendar).SerializeToString();
    }

    private static CalendarEvent FindEvent(IcalCalendar calendar, string uid, string occurrenceKey)
    {
        return calendar.Events.FirstOrDefault(value =>
        {
            if (!string.Equals(value.Uid, uid, StringComparison.Ordinal))
                return false;

            var recurrenceId = value.RecurrenceIdentifier?.StartTime;
            return string.IsNullOrWhiteSpace(occurrenceKey)
                ? recurrenceId == null
                : recurrenceId != null && string.Equals(GetOccurrenceKey(recurrenceId), occurrenceKey, StringComparison.Ordinal);
        });
    }

    private static void ApplyEditableProperties(
        CalendarEvent target,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee> attendees)
    {
        target.Summary = item.Title ?? string.Empty;
        target.Description = item.Description ?? string.Empty;
        target.Location = item.Location ?? string.Empty;
        target.Status = MapStatus(item.Status);
        target.Transparency = item.ShowAs == CalendarItemShowAs.Free ? "TRANSPARENT" : "OPAQUE";
        target.Class = MapVisibility(item.Visibility);
        target.Sequence++;
        target.DtStamp = CalDateTime.UtcNow;
        target.LastModified = CalDateTime.UtcNow;
        if (target.Duration.HasValue)
            target.Duration = null;

        if (item.IsAllDayEvent)
        {
            target.Start = new CalDateTime(DateOnly.FromDateTime(item.StartDate));
            target.End = new CalDateTime(DateOnly.FromDateTime(item.EndDate));
        }
        else
        {
            target.Start = CreateCalDateTime(item.StartDate, item.StartTimeZone);
            if (item.DurationInSeconds > 0)
            {
                target.End = CreateCalDateTime(item.EndDate, item.EndTimeZone ?? item.StartTimeZone);
            }
            else
            {
                target.End = null;
            }
        }

        target.Organizer = string.IsNullOrWhiteSpace(item.OrganizerEmail)
            ? null
            : new Organizer($"mailto:{item.OrganizerEmail}")
            {
                CommonName = item.OrganizerDisplayName ?? item.OrganizerEmail
            };
        target.Attendees = attendees?
            .Where(value => !string.IsNullOrWhiteSpace(value.Email))
            .Select(value => new Attendee(new Uri($"mailto:{value.Email}"))
            {
                CommonName = value.Name ?? value.Email,
                Role = value.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT",
                ParticipationStatus = MapParticipationStatus(value.AttendenceStatus)
            })
            .ToList() ?? [];

        if (target.RecurrenceIdentifier == null)
            ApplyRecurrenceProperties(target, item.Recurrence);
    }

    private static void ApplyRecurrenceProperties(CalendarEvent target, string recurrence)
    {
        if (string.Equals(BuildNormalizedRecurrence(target), recurrence ?? string.Empty, StringComparison.Ordinal))
            return;

        var propertyNames = new[] { "RRULE", "RDATE", "EXDATE", "EXRULE" };
        foreach (var propertyName in propertyNames)
            target.Properties.Clear(propertyName);

        if (string.IsNullOrWhiteSpace(recurrence))
            return;

        var recurrenceLines = recurrence
            .Split(Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var recurrenceCalendar = IcalCalendar.Load($"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Calendar//EN
            BEGIN:VEVENT
            UID:recurrence-template
            DTSTART:20000101T000000Z
            {string.Join("\r\n", recurrenceLines)}
            END:VEVENT
            END:VCALENDAR
            """);
        var source = recurrenceCalendar.Events.Single();

        foreach (var propertyName in propertyNames)
        {
            foreach (var property in source.Properties.AllOf(propertyName))
                target.Properties.Add(property.Copy<CalendarProperty>());
        }
    }

    private static string BuildNormalizedRecurrence(CalendarEvent source)
    {
        var lines = new List<string>();
        if (source.RecurrenceRule != null)
            lines.Add($"RRULE:{source.RecurrenceRule}");

        var exceptionDates = source.ExceptionDates?.GetAllDates()
            .Where(value => value != null)
            .Select(value => value.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture))
            .ToList() ?? [];
        if (exceptionDates.Count > 0)
            lines.Add($"EXDATE:{string.Join(",", exceptionDates)}");

        var recurrenceDates = source.RecurrenceDates?.GetAllPeriods()
            .Where(value => value.StartTime != null)
            .Select(value => value.StartTime.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture))
            .ToList() ?? [];
        if (recurrenceDates.Count > 0)
            lines.Add($"RDATE:{string.Join(",", recurrenceDates)}");

        return string.Join(Constants.CalendarEventRecurrenceRuleSeperator, lines);
    }

    private static CalDateTime CreateCalDateTime(DateTime value, string timeZoneId)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return new CalDateTime(unspecified, true);

        if (string.Equals(timeZoneId, TimeZoneInfo.Utc.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return new CalDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc), "UTC", true);
        }

        return new CalDateTime(unspecified, timeZoneId, true);
    }

    private static CalDateTime ParseOccurrenceKey(string key, CalDateTime masterStart)
    {
        var isUtc = key.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var format = isUtc ? "yyyyMMdd'T'HHmmss'Z'" : "yyyyMMdd'T'HHmmss";
        if (!DateTime.TryParseExact(key, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new InvalidOperationException("The CalDAV occurrence ID is invalid.");

        if (isUtc)
            return new CalDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc), "UTC", true);

        return string.IsNullOrWhiteSpace(masterStart?.TzId)
            ? new CalDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), true)
            : new CalDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), masterStart.TzId, true);
    }

    private static (string Uid, string OccurrenceKey) SplitRemoteEventId(string remoteEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteEventId);
        var separatorIndex = remoteEventId.IndexOf(OccurrenceSeparator, StringComparison.Ordinal);
        return separatorIndex < 0
            ? (remoteEventId, string.Empty)
            : (remoteEventId[..separatorIndex], remoteEventId[(separatorIndex + OccurrenceSeparator.Length)..]);
    }

    private static string GetOccurrenceKey(CalDateTime value)
        => value.IsFloating
            ? value.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
            : value.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string MapStatus(CalendarItemStatus status)
        => status switch
        {
            CalendarItemStatus.Cancelled => "CANCELLED",
            CalendarItemStatus.Tentative => "TENTATIVE",
            _ => "CONFIRMED"
        };

    private static string MapVisibility(CalendarItemVisibility visibility)
        => visibility switch
        {
            CalendarItemVisibility.Public => "PUBLIC",
            CalendarItemVisibility.Private => "PRIVATE",
            CalendarItemVisibility.Confidential => "CONFIDENTIAL",
            _ => null
        };

    private static string MapParticipationStatus(AttendeeStatus status)
        => status switch
        {
            AttendeeStatus.Accepted => "ACCEPTED",
            AttendeeStatus.Declined => "DECLINED",
            AttendeeStatus.Tentative => "TENTATIVE",
            _ => "NEEDS-ACTION"
        };
}
