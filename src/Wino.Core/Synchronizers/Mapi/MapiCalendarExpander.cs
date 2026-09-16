#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mapi;
using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Turns Calendar-folder rows into the flat occurrences the app stores. A single appointment is one
/// occurrence; a series master is expanded over the sync window with the iCal library (the same one
/// the CalDav path uses), honouring the blob's deleted instances and exception overrides. Occurrence
/// ids are "mapi:{mid}:{originalStartUtc}" so an exception keeps the id of the slot it replaced.
/// </summary>
internal static class MapiCalendarExpander
{
    /// <summary>
    /// The series master as a stored (never rendered) row: the rule in the app's RRULE form and the
    /// first occurrence's times. Null for a single appointment or a series whose rule cannot be expressed.
    /// </summary>
    internal static SyncedCalendarEvent? Master(MapiAppointmentInfo row)
    {
        if (!row.IsRecurring || row.Recurrence == null)
            return null;

        var (zone, iana) = ResolveZone(row.TimeZoneKeyName);

        string rule;
        try { rule = "RRULE:" + row.Recurrence.ToRRule(); }
        catch (MapiFormatException) { return null; }

        var start = row.Recurrence.FirstStart;
        var master = Build(row, MapiExchangeSynchronizer.ToMailCopyId(row.MessageId), ToUtc(start, zone), ToUtc(start + row.Recurrence.Duration, zone), iana, row.Subject, row.Location, row.BusyStatus, row.AllDay);
        master.Recurrence = rule;
        return master;
    }

    internal static List<SyncedCalendarEvent> Expand(MapiAppointmentInfo row, DateTime windowStartUtc, DateTime windowEndUtc, Action<string>? diagnostics = null)
    {
        var result = new List<SyncedCalendarEvent>();
        var baseId = MapiExchangeSynchronizer.ToMailCopyId(row.MessageId);
        var iana = ToIana(row.TimeZoneKeyName);

        if (!row.IsRecurring || row.Recurrence == null)
        {
            if (row.StartUtc is not { } start || row.EndUtc is not { } end)
                return result;

            if (end < windowStartUtc || start > windowEndUtc)
                return result;

            result.Add(Build(row, WithTracking(baseId, row.ClientTrackingId), start, end, iana, row.Subject, row.Location, row.BusyStatus, row.AllDay));
            return result;
        }

        var recurrence = row.Recurrence;
        TimeZoneInfo zone;
        (zone, iana) = ResolveZone(row.TimeZoneKeyName);

        var seriesStart = recurrence.FirstStart;
        var duration = recurrence.Duration;
        var master = new CalendarEvent
        {
            Start = new CalDateTime(seriesStart, iana),
            End = new CalDateTime(seriesStart + duration, iana),
        };

        try
        {
            master.RecurrenceRules.Add(new RecurrencePattern(recurrence.ToRRule()));
        }
        catch (Exception ex)
        {
            diagnostics?.Invoke($"appointment 0x{row.MessageId:X16}: RRULE rejected ({ex.Message}); series shown as its first occurrence only");
            result.Add(Build(row, baseId, ToUtc(seriesStart, zone), ToUtc(seriesStart + duration, zone), iana, row.Subject, row.Location, row.BusyStatus, row.AllDay));
            return result;
        }

        var deleted = new HashSet<DateTime>(recurrence.DeletedInstanceDates.Select(d => d.Date));
        var exceptions = recurrence.Exceptions.ToDictionary(e => e.OriginalStart.Date, e => e);

        var options = new Ical.Net.Evaluation.EvaluationOptions { MaxUnmatchedIncrementsLimit = 10_000 };
        var occurrences = master
            .GetOccurrences(new CalDateTime(windowStartUtc.AddDays(-1), true), options)
            .TakeWhile(o => o.Period.StartTime.AsUtc < windowEndUtc);

        foreach (var occurrence in occurrences)
        {
            var originalStartUtc = occurrence.Period.StartTime.AsUtc;
            var originalWall = TimeZoneInfo.ConvertTimeFromUtc(originalStartUtc, zone);

            // MS-OXOCAL 2.2.1.44.1: a modified instance's original date is listed under the deleted dates
            // too (the slot is vacated, the exception re-occupies it), so deleted means deleted only when
            // no exception claims the date.
            var hasException = exceptions.TryGetValue(originalWall.Date, out var exception);
            if (deleted.Contains(originalWall.Date) && !hasException)
                continue;

            var startUtc = originalStartUtc;
            var endUtc = originalStartUtc + duration;
            string? subject = row.Subject, location = row.Location;
            var busy = row.BusyStatus;
            var allDay = row.AllDay;

            if (hasException)
            {
                startUtc = ToUtc(exception!.Start, zone);
                endUtc = ToUtc(exception.End, zone);
                subject = exception.Subject ?? subject;
                location = exception.Location ?? location;
                busy = exception.BusyStatus ?? busy;
                allDay = exception.AllDay ?? allDay;
            }

            if (endUtc < windowStartUtc || startUtc > windowEndUtc)
                continue;

            var id = $"{baseId}:{originalStartUtc:yyyyMMdd'T'HHmmss'Z'}";
            var occurrenceEvent = Build(row, id, startUtc, endUtc, iana, subject, location, busy, allDay);
            occurrenceEvent.RecurringParentRemoteId = baseId;
            result.Add(occurrenceEvent);
        }

        return result;
    }

    private static SyncedCalendarEvent Build(MapiAppointmentInfo row, string remoteId, DateTime startUtc, DateTime endUtc, string? iana, string? subject, string? location, uint? busy, bool allDay) => new()
    {
        RemoteId = remoteId,
        Title = subject,
        Description = row.Body,
        Location = location,
        StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
        EndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc),
        TimeZoneIana = iana,
        IsAllDay = allDay,
        OrganizerEmail = row.OrganizerAddress,
        OrganizerName = row.OrganizerName,
        CreatedAt = row.Created is { } c ? new DateTimeOffset(DateTime.SpecifyKind(c, DateTimeKind.Utc)) : DateTimeOffset.UtcNow,
        UpdatedAt = row.Modified is { } m ? new DateTimeOffset(DateTime.SpecifyKind(m, DateTimeKind.Utc)) : DateTimeOffset.UtcNow,
        Visibility = row.Sensitivity switch
        {
            1 or 2 => CalendarItemVisibility.Private,
            3 => CalendarItemVisibility.Confidential,
            _ => CalendarItemVisibility.Public
        },
        ShowAs = busy switch
        {
            0 => CalendarItemShowAs.Free,
            1 => CalendarItemShowAs.Tentative,
            3 => CalendarItemShowAs.OutOfOffice,
            4 => CalendarItemShowAs.WorkingElsewhere,
            _ => CalendarItemShowAs.Busy
        },
        // PidLidResponseStatus: 0 none, 1 organizer, 2 tentative, 3 accepted, 4 declined, 5 not responded.
        MyResponse = row.IsCancelled ? CalendarItemStatus.Cancelled : row.ResponseStatus switch
        {
            2 => CalendarItemStatus.Tentative,
            4 => CalendarItemStatus.Cancelled,
            5 => CalendarItemStatus.NotResponded,
            _ => CalendarItemStatus.Accepted
        },
        ReminderMinutesBeforeStart = row.ReminderSet ? (int?)(row.ReminderMinutes ?? 15) : null,
        Attendees = MapAttendees(row.Attendees)
    };

    /// <summary>Recipient rows to attendees; the organizer row is not an attendee (EWS listed only Required/Optional).</summary>
    internal static List<SyncedCalendarAttendee>? MapAttendees(List<OpenRecipient>? recipients)
    {
        if (recipients == null || recipients.Count == 0)
            return null;

        var attendees = recipients
            .Where(r => !r.IsOrganizer && !string.IsNullOrEmpty(r.SmtpAddress))
            .Select(r => new SyncedCalendarAttendee
            {
                Name = r.Name ?? r.SmtpAddress,
                Email = r.SmtpAddress,
                IsOptional = r.IsOptional,
                Status = r.TrackStatus switch
                {
                    2 => AttendeeStatus.Tentative,
                    3 => AttendeeStatus.Accepted,
                    4 => AttendeeStatus.Declined,
                    _ => AttendeeStatus.NeedsAction
                }
            })
            .ToList();

        return attendees.Count == 0 ? null : attendees;
    }

    private static string WithTracking(string baseId, string? clientTrackingId)
        => clientTrackingId != null && Guid.TryParseExact(clientTrackingId, "N", out var guid) ? baseId.WithClientTrackingId(guid) : baseId;

    /// <summary>
    /// The zone an appointment's times are in, and the IANA id to store beside them. A key the platform
    /// cannot resolve falls back to UTC with no stored zone, so the times are still right.
    /// </summary>
    private static (TimeZoneInfo Zone, string? Iana) ResolveZone(string? windowsKey)
    {
        var iana = ToIana(windowsKey);
        if (iana == null)
            return (TimeZoneInfo.Utc, null);

        try
        {
            return (TimeZoneInfo.FindSystemTimeZoneById(iana), iana);
        }
        catch
        {
            return (TimeZoneInfo.Utc, null);
        }
    }

    internal static string? ToIana(string? windowsKey)
    {
        if (string.IsNullOrEmpty(windowsKey))
            return null;

        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsKey, out var iana))
                return iana;
            TimeZoneInfo.FindSystemTimeZoneById(windowsKey);
            return windowsKey;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime ToUtc(DateTime wallClock, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), zone);
}
