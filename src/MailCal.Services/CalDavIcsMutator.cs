using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Enums;
using IcalCalendar = Ical.Net.Calendar;

namespace MailCal.Services;

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
        => UpdateEvent(existingIcs, item, attendees, identity: null);

    /// <summary>
    /// Applies the user's edits to the stored resource. With <paramref name="identity"/>, an event the
    /// user organizes gets an ORGANIZER from the server's calendar-user-address-set. Attendees that are
    /// already on the event keep their PARTSTAT and scheduling parameters from the stored copy: only
    /// they (through the server) change their answer, never the organizer's edit.
    /// </summary>
    public static string UpdateEvent(
        string existingIcs,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee> attendees,
        CalDavUserIdentity identity)
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

        ApplyEditableProperties(target, item, attendees, identity);
        return new CalendarSerializer(calendar).SerializeToString();
    }

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
    public static string SetOwnParticipation(
        string existingIcs,
        string remoteEventId,
        CalDavUserIdentity identity,
        AttendeeStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        ArgumentNullException.ThrowIfNull(identity);

        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var (uid, occurrenceKey) = SplitRemoteEventId(remoteEventId);
        var target = FindEvent(calendar, uid, occurrenceKey);

        if (target == null && !string.IsNullOrWhiteSpace(occurrenceKey))
        {
            var master = FindMaster(calendar, uid)
                ?? throw new InvalidOperationException("The recurring CalDAV master event is missing from the stored resource.");

            target = CreateOccurrenceException(master, occurrenceKey);
            calendar.Events.Add(target);
        }

        if (target == null)
            throw new InvalidOperationException("The CalDAV event is missing from the stored resource.");

        var partStat = MapParticipationStatus(status);
        SetOwnPartStat(target, identity, partStat, addWhenMissing: true);

        if (target.RecurrenceIdentifier == null)
        {
            foreach (var exception in calendar.Events.Where(value =>
                         string.Equals(value.Uid, uid, StringComparison.Ordinal) &&
                         value.RecurrenceIdentifier != null))
            {
                // An exception without the user means the organizer removed them from that instance.
                SetOwnPartStat(exception, identity, partStat, addWhenMissing: false);
            }
        }

        return new CalendarSerializer(calendar).SerializeToString();
    }

    private static void SetOwnPartStat(CalendarEvent target, CalDavUserIdentity identity, string partStat, bool addWhenMissing)
    {
        var own = target.Attendees.Where(attendee => identity.IsUser(attendee.Value)).ToList();

        if (own.Count == 0)
        {
            if (!addWhenMissing || string.IsNullOrWhiteSpace(identity.OrganizerAddress))
                return;

            target.Attendees.Add(new Attendee(new Uri($"mailto:{identity.OrganizerAddress}"))
            {
                Role = "REQ-PARTICIPANT",
                ParticipationStatus = partStat
            });
        }

        foreach (var attendee in own)
        {
            attendee.ParticipationStatus = partStat;
        }

        target.DtStamp = CalDateTime.UtcNow;
    }

    /// <summary>
    /// A copy of the master for one instance: no recurrence rules, RECURRENCE-ID and DTSTART at the
    /// instance (a DATE for all-day series) and the master's length.
    /// </summary>
    private static CalendarEvent CreateOccurrenceException(CalendarEvent master, string occurrenceKey)
    {
        var recurrenceId = ParseOccurrenceKey(occurrenceKey, master.Start);
        if (master.Start != null && !master.Start.HasTime)
            recurrenceId = new CalDateTime(DateOnly.FromDateTime(recurrenceId.Value));

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
        ReplaceAttendees(exception, master.Attendees.Select(CloneAttendee));
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
        {
            // Ical.Net's event collection matches on component equality, which can resolve to the
            // series master that shares the UID. Rebuild the list so only this instance is dropped.
            var remainingEvents = calendar.Events.Where(value => !ReferenceEquals(value, existingException)).ToList();
            calendar.Events.Clear();
            foreach (var remainingEvent in remainingEvents)
                calendar.Events.Add(remainingEvent);
        }

        master.ExceptionDates.Add(recurrenceId);
        master.Sequence++;
        master.DtStamp = CalDateTime.UtcNow;
        master.LastModified = CalDateTime.UtcNow;

        return new CalendarSerializer(calendar).SerializeToString();
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

    private static void ApplyEditableProperties(
        CalendarEvent target,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee> attendees,
        CalDavUserIdentity identity)
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

        var hasAttendees = attendees?.Any(value => !string.IsNullOrWhiteSpace(value.Email)) == true;
        ApplyOrganizer(target, item, identity, hasAttendees);

        // Only the attendee changes their PARTSTAT. Lines already on the event keep it, with their
        // scheduling parameters; only new attendees get the status of their row.
        var storedAttendees = target.Attendees.ToList();
        var mergedAttendees = attendees?
            .Where(value => !string.IsNullOrWhiteSpace(value.Email))
            .Select(value => MergeAttendee(value, storedAttendees))
            .ToList() ?? [];

        // Assigning Attendees replaces only the first ATTENDEE property, so every line is
        // removed first and the list added line by line.
        ReplaceAttendees(target, mergedAttendees);

        if (target.RecurrenceIdentifier == null)
            ApplyRecurrenceProperties(target, item.Recurrence);
    }

    private static void ApplyOrganizer(CalendarEvent target, CalendarItem item, CalDavUserIdentity identity, bool hasAttendees)
    {
        var desired = item.OrganizerEmail?.Trim() ?? string.Empty;
        var existing = target.Organizer;
        var existingEmail = CalDavUserIdentity.ExtractEmail(existing?.Value?.OriginalString);

        // Same organizer: keep the stored property and its parameters (SCHEDULE-STATUS, SENT-BY).
        if (existing != null && desired.Length > 0 && string.Equals(existingEmail, desired, StringComparison.OrdinalIgnoreCase))
            return;

        if (identity != null)
        {
            // The stored organizer is the user under another address of the set: keep it.
            if (existing != null && identity.IsUser(existing.Value) && (desired.Length == 0 || identity.IsUser(desired)))
                return;

            // The user organizes: write the address the server knows the user by.
            if ((desired.Length > 0 && identity.IsUser(desired)) || (desired.Length == 0 && hasAttendees))
                desired = identity.ResolveOrganizerAddress(desired);
        }

        target.Organizer = string.IsNullOrWhiteSpace(desired)
            ? null
            : new Organizer($"mailto:{desired}")
            {
                CommonName = string.IsNullOrWhiteSpace(item.OrganizerDisplayName) ? desired : item.OrganizerDisplayName
            };
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

    private static Attendee MergeAttendee(CalendarEventAttendee value, List<Attendee> storedAttendees)
    {
        var stored = storedAttendees.FirstOrDefault(attendee => string.Equals(
            CalDavUserIdentity.ExtractEmail(attendee.Value?.OriginalString),
            value.Email.Trim(),
            StringComparison.OrdinalIgnoreCase));

        if (stored == null)
        {
            return new Attendee(new Uri($"mailto:{value.Email}"))
            {
                CommonName = value.Name ?? value.Email,
                Role = value.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT",
                ParticipationStatus = MapParticipationStatus(value.AttendenceStatus)
            };
        }

        var merged = CloneAttendee(stored);

        // The organizer's own line (ROLE=CHAIR) keeps its role.
        if (!string.Equals(merged.Role, "CHAIR", StringComparison.OrdinalIgnoreCase))
            merged.Role = value.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT";

        if (!string.IsNullOrWhiteSpace(value.Name) && !string.Equals(value.Name, value.Email, StringComparison.OrdinalIgnoreCase))
            merged.CommonName = value.Name;

        return merged;
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
            PRODID:-//Mail-Cal//Calendar//EN
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
