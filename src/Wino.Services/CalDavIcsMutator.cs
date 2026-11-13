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

public static partial class CalDavIcsMutator
{
    private const string OccurrenceSeparator = "::";

    public static string Normalize(string icsContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icsContent);
        var calendar = IcalCalendar.Load(icsContent)
            ?? throw new InvalidOperationException("The generated CalDAV resource is not a valid iCalendar object.");

        foreach (var calendarEvent in calendar.Events.Where(value => value.RecurrenceIdentifier == null))
            AlignExceptionDates(calendarEvent);

        return Serialize(calendar);
    }

    public static string UpdateEvent(
        string existingIcs,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee> attendees)
        => UpdateEvent(existingIcs, item, attendees, identity: null);

    /// <summary>
    /// Applies the user's edits to the stored resource and returns the new text; see <see cref="EditEvent"/>.
    /// </summary>
    public static string UpdateEvent(
        string existingIcs,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee> attendees,
        CalDavUserIdentity identity)
        => EditEvent(existingIcs, item, attendees, identity).IcsContent;

    /// <summary>
    /// True when the event (or, for an occurrence without its own exception, the series master) has
    /// an ORGANIZER that is not the user, which makes the user an attendee.
    /// </summary>
    public static bool IsOrganizedByOther(string existingIcs, string remoteEventId, CalDavUserIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        ArgumentNullException.ThrowIfNull(identity);

        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var (uid, occurrenceKey) = SplitRemoteEventId(remoteEventId);
        var target = FindEvent(calendar, uid, occurrenceKey) ?? FindMaster(calendar, uid);
        var organizer = target?.Organizer?.Value;

        return organizer != null && !identity.IsUser(organizer);
    }

    /// <summary>
    /// Writes the user's answer to an invitation the way RFC 6638 expects from an attendee: only the
    /// PARTSTAT of the user's own ATTENDEE line changes (and DTSTAMP is refreshed). STATUS, SEQUENCE
    /// and every other property stay as the server has them; the server then sends the reply.
    /// Answering the series also answers every exception that lists the user. Answering one
    /// occurrence adds an exception for it when there is none yet.
    /// </summary>
    /// <param name="attendeeAddress">
    /// The address added as the user's ATTENDEE when the event does not list the user (a forwarded
    /// invitation); the identity's ORGANIZER address when empty.
    /// </param>
    public static string SetOwnParticipation(
        string existingIcs,
        string remoteEventId,
        CalDavUserIdentity identity,
        AttendeeStatus status,
        string attendeeAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        ArgumentNullException.ThrowIfNull(identity);

        var addedAddress = string.IsNullOrWhiteSpace(attendeeAddress) ? identity.OrganizerAddress : attendeeAddress.Trim();
        return SetOwnParticipationLines(existingIcs, remoteEventId, identity, status, addedAddress);
    }

    /// <summary>
    /// A copy of the master for one instance: no recurrence rules, RECURRENCE-ID and DTSTART at the
    /// instance (a DATE for all-day series) and the master's length.
    /// </summary>
    private static CalendarEvent CreateOccurrenceException(CalendarEvent master, string occurrenceKey)
    {
        var recurrenceId = ToSeriesValueType(ParseOccurrenceKey(occurrenceKey, master.Start), master.Start);

        var length = master.End != null && master.Start != null
            ? master.End.Value - master.Start.Value
            : master.Duration?.ToTimeSpanUnspecified() ?? (master.Start?.HasTime == false ? TimeSpan.FromDays(1) : TimeSpan.Zero);

        var exception = master.Copy<CalendarEvent>();
        exception.Properties.Clear("RRULE");
        exception.Properties.Clear("EXRULE");
        exception.Properties.Clear("RDATE");
        exception.Properties.Clear("EXDATE");
        exception.RecurrenceIdentifier = new RecurrenceIdentifier(recurrenceId, RecurrenceRange.ThisInstance);

        // The copy shares attendee objects with the master; the instance gets its own lines.
        ReplaceAttendees(exception, master.Attendees.Where(attendee => attendee?.Value != null).Select(CloneAttendee));
        if (exception.Duration.HasValue)
            exception.Duration = null;

        exception.Start = recurrenceId;
        exception.End = Shift(recurrenceId, length);

        return exception;
    }

    private static CalDateTime Shift(CalDateTime start, TimeSpan length)
    {
        var value = start.Value.Add(length);

        if (!start.HasTime)
            return new CalDateTime(DateOnly.FromDateTime(value));

        return string.IsNullOrWhiteSpace(start.TzId)
            ? new CalDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), true)
            : new CalDateTime(DateTime.SpecifyKind(value, start.TzId == "UTC" ? DateTimeKind.Utc : DateTimeKind.Unspecified), start.TzId, true);
    }

    private static CalendarEvent FindMaster(IcalCalendar calendar, string uid)
        => calendar.Events.FirstOrDefault(value =>
            string.Equals(value.Uid, uid, StringComparison.Ordinal) && value.RecurrenceIdentifier == null);

    /// <param name="identity">The user, to tell whether the user organizes the series; without it SEQUENCE always goes up.</param>
    public static string RemoveOccurrence(string existingIcs, string remoteEventId, CalDavUserIdentity identity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        return RemoveOccurrenceLines(existingIcs, remoteEventId, identity);
    }

    private static CalendarEvent FindEvent(IcalCalendar calendar, string uid, string occurrenceKey)
    {
        // Occurrence keys are expressed in the master's value type, so a RECURRENCE-ID
        // written with a different value type still resolves to the same exception.
        var masterStart = calendar.Events.FirstOrDefault(value =>
            string.Equals(value.Uid, uid, StringComparison.Ordinal) && value.RecurrenceIdentifier == null)?.Start;

        return calendar.Events.FirstOrDefault(value =>
        {
            if (!string.Equals(value.Uid, uid, StringComparison.Ordinal))
                return false;

            var recurrenceId = value.RecurrenceIdentifier?.StartTime;
            return string.IsNullOrWhiteSpace(occurrenceKey)
                ? recurrenceId == null
                : recurrenceId != null && string.Equals(CalDavOccurrenceKey.Create(recurrenceId, masterStart), occurrenceKey, StringComparison.Ordinal);
        });
    }

    /// <summary>A new ATTENDEE line with the value and parameters of <paramref name="source"/>.</summary>
    private static Attendee CloneAttendee(Attendee source)
    {
        var clone = new Attendee(source.Value);
        foreach (var parameter in source.Parameters.ToList())
            clone.Parameters.Set(parameter.Name, parameter.Values.ToList());

        return clone;
    }

    private static void ReplaceAttendees(CalendarEvent target, IEnumerable<Attendee> attendees)
    {
        var lines = attendees.ToList();

        target.Properties.Clear("ATTENDEE");
        foreach (var attendee in lines)
            target.Attendees.Add(attendee);
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

        AlignExceptionDates(target);
    }

    /// <summary>
    /// Rewrites EXDATE values in the value type of the series DTSTART. The stored recurrence string
    /// carries them in UTC, and sabre/dav and iCloud do not match a UTC EXDATE against a zoned or
    /// all-day series.
    /// </summary>
    private static void AlignExceptionDates(CalendarEvent master)
    {
        var dates = master.ExceptionDates.GetAllDates().Where(value => value != null).ToList();
        var aligned = dates.Select(value => ToSeriesValueType(value, master.Start)).ToList();

        if (dates.Count == 0 || dates.Zip(aligned).All(pair => ReferenceEquals(pair.First, pair.Second)))
            return;

        master.Properties.Clear("EXDATE");
        master.ExceptionDates.AddRange(aligned);
    }

    /// <summary>
    /// An instance value (RECURRENCE-ID, EXDATE) in the value type of the series DTSTART: a DATE for
    /// all-day series and the master's TZID for zoned ones. Floating series stay as they are.
    /// </summary>
    private static CalDateTime ToSeriesValueType(CalDateTime value, CalDateTime masterStart)
    {
        if (value == null || masterStart == null)
            return value;

        if (!masterStart.HasTime)
            return value.HasTime ? new CalDateTime(DateOnly.FromDateTime(value.Value)) : value;

        if (!value.HasTime || value.IsFloating || masterStart.IsFloating ||
            string.Equals(value.TzId, masterStart.TzId, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToTimeZone(masterStart.TzId);
    }

    private static string Serialize(IcalCalendar calendar)
    {
        CalDavTimeZones.AddMissingTimeZones(calendar);
        return new CalendarSerializer(calendar).SerializeToString();
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

    /// <summary>
    /// A wall-clock time in its zone. A stored event keeps its own TZID (defined by its own
    /// VTIMEZONE) while the zone is unchanged; otherwise the IANA id is written, since other
    /// clients do not know Windows ids. A zone that has no IANA id is written in UTC.
    /// </summary>
    private static CalDateTime CreateCalDateTime(DateTime value, string timeZoneId, CalDateTime stored)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return new CalDateTime(unspecified, true);

        if (stored is { HasTime: true } && !string.IsNullOrWhiteSpace(stored.TzId) && CalDavTimeZones.IsSameZone(stored.TzId, timeZoneId))
            return CreateZoned(unspecified, stored.TzId);

        var icsTimeZoneId = CalDavTimeZones.ToIcsTimeZoneId(timeZoneId);
        if (icsTimeZoneId != null)
            return CreateZoned(unspecified, icsTimeZoneId);

        try
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
            return new CalDateTime(utc, CalDavTimeZones.UtcTzId, true);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return new CalDateTime(unspecified, timeZoneId, true);
        }
    }

    private static CalDateTime CreateZoned(DateTime unspecified, string tzId)
        => CalDavTimeZones.IsUtc(tzId)
            ? new CalDateTime(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), CalDavTimeZones.UtcTzId, true)
            : new CalDateTime(unspecified, tzId, true);

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
