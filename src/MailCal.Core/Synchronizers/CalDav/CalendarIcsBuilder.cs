using System;
using System.Collections.Generic;
using System.Linq;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Builds the iCalendar text of a new event and fills the defaults every stored calendar item
/// needs. Shared by the CalDAV engine and the local calendar handler.
/// </summary>
internal static partial class CalendarIcsBuilder
{
    public static void EnsureCalendarItemDefaults(CalendarItem item, MailAccount account, string idPrefix)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        if (item.Id == Guid.Empty)
            item.Id = Guid.NewGuid();

        if (string.IsNullOrWhiteSpace(item.RemoteEventId))
            item.RemoteEventId = $"{idPrefix}-{item.Id:N}";

        if (item.CreatedAt == default)
            item.CreatedAt = DateTimeOffset.UtcNow;

        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.OrganizerDisplayName ??= account?.SenderName ?? string.Empty;
        item.OrganizerEmail ??= account?.Address ?? string.Empty;
        item.StartTimeZone ??= TimeZoneInfo.Local.Id;
        item.EndTimeZone ??= item.StartTimeZone;
    }

    public static string BuildIcsContent(CalendarItem item, List<CalendarEventAttendee> attendees)
    {
        var uid = item.RemoteEventId?.Split(new[] { "::" }, StringSplitOptions.None)[0] ?? item.Id.ToString("N");
        var dtStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Mail-Cal//Calendar//EN",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{EscapeIcs(uid)}",
            $"DTSTAMP:{dtStamp}",
        };

        if (item.IsAllDayEvent)
        {
            lines.Add($"DTSTART;VALUE=DATE:{item.StartDate:yyyyMMdd}");
            lines.Add($"DTEND;VALUE=DATE:{item.EndDate:yyyyMMdd}");
        }
        else
        {
            var startUtc = ConvertEventTimeToUtc(item.StartDate, item.StartTimeZone);
            var endUtc = ConvertEventTimeToUtc(item.EndDate, item.EndTimeZone ?? item.StartTimeZone);

            lines.Add($"DTSTART:{startUtc:yyyyMMdd'T'HHmmss'Z'}");
            lines.Add($"DTEND:{endUtc:yyyyMMdd'T'HHmmss'Z'}");
        }

        if (!string.IsNullOrWhiteSpace(item.Title))
            lines.Add($"SUMMARY:{EscapeIcs(item.Title)}");

        if (!string.IsNullOrWhiteSpace(item.Description))
            lines.Add($"DESCRIPTION:{EscapeIcs(item.Description)}");

        if (!string.IsNullOrWhiteSpace(item.Location))
            lines.Add($"LOCATION:{EscapeIcs(item.Location)}");

        lines.Add($"STATUS:{MapStatus(item.Status)}");
        lines.Add($"TRANSP:{(item.ShowAs == CalendarItemShowAs.Free ? "TRANSPARENT" : "OPAQUE")}");
        lines.Add($"CLASS:{MapVisibility(item.Visibility)}");

        if (!string.IsNullOrWhiteSpace(item.Recurrence))
        {
            var recurrenceLines = item.Recurrence
                .Split(MailCal.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l));

            lines.AddRange(recurrenceLines);
        }

        if (!string.IsNullOrWhiteSpace(item.OrganizerEmail))
        {
            var organizerName = string.IsNullOrWhiteSpace(item.OrganizerDisplayName)
                ? item.OrganizerEmail
                : item.OrganizerDisplayName;
            lines.Add($"ORGANIZER;CN={EscapeIcs(organizerName)}:mailto:{EscapeIcs(item.OrganizerEmail)}");
        }

        if (attendees != null)
        {
            foreach (var attendee in attendees.Where(a => !string.IsNullOrWhiteSpace(a.Email)))
            {
                var role = attendee.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT";
                var partStat = attendee.AttendenceStatus switch
                {
                    AttendeeStatus.Accepted => "ACCEPTED",
                    AttendeeStatus.Declined => "DECLINED",
                    AttendeeStatus.Tentative => "TENTATIVE",
                    _ => "NEEDS-ACTION"
                };

                var cn = string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name;
                lines.Add($"ATTENDEE;CN={EscapeIcs(cn)};ROLE={role};PARTSTAT={partStat}:mailto:{EscapeIcs(attendee.Email)}");
            }
        }

        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        return string.Join(Environment.NewLine, lines);
    }

    private static DateTime ConvertEventTimeToUtc(DateTime eventDateTime, string eventTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(eventTimeZoneId))
            return eventDateTime.ToUniversalTime();

        try
        {
            var eventTimeZone = TimeZoneInfo.FindSystemTimeZoneById(eventTimeZoneId);
            var unspecifiedDateTime = DateTime.SpecifyKind(eventDateTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecifiedDateTime, eventTimeZone);
        }
        catch
        {
            return eventDateTime.ToUniversalTime();
        }
    }

    private static string EscapeIcs(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string MapStatus(CalendarItemStatus status)
    {
        return status switch
        {
            CalendarItemStatus.Cancelled => "CANCELLED",
            CalendarItemStatus.Tentative => "TENTATIVE",
            _ => "CONFIRMED"
        };
    }

    private static string MapVisibility(CalendarItemVisibility visibility)
    {
        return visibility switch
        {
            CalendarItemVisibility.Public => "PUBLIC",
            CalendarItemVisibility.Private => "PRIVATE",
            CalendarItemVisibility.Confidential => "CONFIDENTIAL",
            _ => "PUBLIC"
        };
    }
}
