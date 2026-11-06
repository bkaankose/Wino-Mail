using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Ical.Net;
using Ical.Net.DataTypes;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Synchronizers.Eas;

internal static partial class EasDataMapper
{
    internal static Guid? ClientEventId(XElement data)
        => Guid.TryParse(Value(data, Calendar, "UID"), out var id) ? id : null;

    internal static CalendarItem ReadEvent(XElement data, AccountCalendar calendar, string remoteId)
    {
        var start = Date(Value(data, Calendar, "StartTime")) ?? throw new InvalidDataException("An EAS event has no start time.");
        var end = Date(Value(data, Calendar, "EndTime")) ?? throw new InvalidDataException("An EAS event has no end time.");
        if (end < start)
            throw new InvalidDataException("An EAS event ends before its start time.");
        var zone = EasTimeZone.Read(data, start.Year);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(start, zone);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(end, zone);

        return new CalendarItem
        {
            Id = StableId(Key(calendar.AccountId, calendar.RemoteCalendarId, remoteId)), CalendarId = calendar.Id, AssignedCalendar = calendar,
            RemoteEventId = remoteId, Title = Value(data, Calendar, "Subject"), Description = BodyText(data),
            Location = Value(data, Calendar, "Location"), StartDate = localStart, DurationInSeconds = (localEnd - localStart).TotalSeconds,
            StartTimeZone = zone.Id, EndTimeZone = zone.Id, OrganizerEmail = Value(data, Calendar, "OrganizerEmail"),
            OrganizerDisplayName = Value(data, Calendar, "OrganizerName"), CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = Date(Value(data, Calendar, "DtStamp")) ?? DateTime.UtcNow,
            Recurrence = ReadRecurrence(data.Element(Calendar + "Recurrence")) + string.Concat(
                data.Element(Calendar + "Exceptions")?.Elements(Calendar + "Exception").Select(exception =>
                    Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator + "EXDATE:" + Value(exception, Calendar, "ExceptionStartTime")) ?? Enumerable.Empty<string>()),
            ShowAs = Value(data, Calendar, "BusyStatus") switch { "0" => CalendarItemShowAs.Free, "1" => CalendarItemShowAs.Tentative,
                "3" => CalendarItemShowAs.OutOfOffice, "4" => CalendarItemShowAs.WorkingElsewhere, _ => CalendarItemShowAs.Busy }
        };
    }

    internal static List<CalendarEventAttendee> ReadAttendees(XElement data, Guid eventId)
        => data.Element(Calendar + "Attendees")?.Elements(Calendar + "Attendee").Select(attendee => new CalendarEventAttendee
        {
            Id = StableId(eventId.ToString("N") + Value(attendee, Calendar, "Email")), CalendarItemId = eventId,
            Email = Value(attendee, Calendar, "Email"), Name = Value(attendee, Calendar, "Name"),
            IsOptionalAttendee = Value(attendee, Calendar, "AttendeeType") == "2",
            AttendenceStatus = Value(attendee, Calendar, "AttendeeStatus") switch
            { "2" => AttendeeStatus.Tentative, "3" => AttendeeStatus.Accepted, "4" => AttendeeStatus.Declined, _ => AttendeeStatus.NeedsAction }
        }).ToList() ?? [];

    internal static XElement WriteEvent(CalendarItem item, IReadOnlyList<CalendarEventAttendee> attendees, IReadOnlyList<Reminder> reminders)
    {
        var zone = string.IsNullOrEmpty(item.StartTimeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(item.StartTimeZone);
        var start = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(item.StartDate, DateTimeKind.Unspecified), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(item.EndDate, DateTimeKind.Unspecified), zone);
        if (item.IsAllDayEvent)
        {
            start = DateTime.SpecifyKind(item.StartDate.Date, DateTimeKind.Utc);
            end = DateTime.SpecifyKind(item.EndDate.Date, DateTimeKind.Utc);
        }
        var result = new XElement(Sync + "ApplicationData",
            new XElement(Calendar + "Subject", item.Title ?? string.Empty), new XElement(Calendar + "Location", item.Location ?? string.Empty),
            new XElement(Calendar + "StartTime", start.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)),
            new XElement(Calendar + "EndTime", end.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)),
            new XElement(Calendar + "DtStamp", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)),
            new XElement(Calendar + "AllDayEvent", item.IsAllDayEvent ? "1" : "0"), new XElement(Calendar + "BusyStatus", (int)item.ShowAs),
            new XElement(Calendar + "TimeZone", EasTimeZone.Write(zone, item.StartDate.Year)),
            TextBody(item.Description), new XElement(Calendar + "Attendees", attendees.Where(attendee => !attendee.IsOrganizer).Select(attendee =>
                new XElement(Calendar + "Attendee", new XElement(Calendar + "Email", attendee.Email), new XElement(Calendar + "Name", attendee.Name ?? attendee.Email),
                    new XElement(Calendar + "AttendeeType", attendee.IsOptionalAttendee ? "2" : "1")))));
        if (reminders.Count > 0)
            result.Add(new XElement(Calendar + "Reminder", Math.Max(0, reminders.Min(reminder => reminder.DurationInSeconds) / 60)));

        if (!string.IsNullOrEmpty(item.Recurrence))
        {
            var rule = item.Recurrence.Split(new[] { Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator, "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("RRULE:", StringComparison.Ordinal));
            if (rule == null)
                throw new NotSupportedException("The event recurrence is not an RRULE.");

            var pattern = new RecurrencePattern(rule[6..].Trim());
            var relative = pattern.ByDay.Count > 0;
            var type = pattern.Frequency switch
            {
                FrequencyType.Daily => 0, FrequencyType.Weekly => 1,
                FrequencyType.Monthly => relative ? 3 : 2, FrequencyType.Yearly => relative ? 6 : 5,
                _ => throw new NotSupportedException("The event recurrence cannot be represented by EAS.")
            };
            var recurrence = new XElement(Calendar + "Recurrence", new XElement(Calendar + "Type", type),
                new XElement(Calendar + "Interval", pattern.Interval));
            if (pattern.Count > 0)
                recurrence.Add(new XElement(Calendar + "Occurrences", pattern.Count));
            if (pattern.Until != default)
                recurrence.Add(new XElement(Calendar + "Until", (item.IsAllDayEvent ? pattern.Until.AsUtc.Date : pattern.Until.AsUtc).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
            if (type is 1 or 3 or 6)
            {
                var days = pattern.ByDay.Count == 0 ? 1 << (int)item.StartDate.DayOfWeek : pattern.ByDay.Aggregate(0, (mask, day) => mask | (1 << (int)day.DayOfWeek));
                recurrence.Add(new XElement(Calendar + "DayOfWeek", days));
            }
            if (type is 3 or 6)
            {
                var offset = pattern.ByDay.First().Offset;
                if (offset == 0 || offset < -1 || offset > 4)
                    throw new NotSupportedException("The event's ordinal weekday cannot be represented by EAS.");

                recurrence.Add(new XElement(Calendar + "WeekOfMonth", offset == -1 ? 5 : offset));
            }
            if (type is 2 or 5)
                recurrence.Add(new XElement(Calendar + "DayOfMonth", pattern.ByMonthDay.FirstOrDefault(item.StartDate.Day)));
            if (type is 5 or 6)
                recurrence.Add(new XElement(Calendar + "MonthOfYear", pattern.ByMonth.FirstOrDefault(item.StartDate.Month)));

            result.Add(recurrence);
        }

        if (item.IsAllDayEvent)
            result.Element(Calendar + "TimeZone")?.Remove();

        return result;
    }

    private static string ReadRecurrence(XElement? recurrence)
    {
        if (recurrence == null || !recurrence.HasElements)
            return string.Empty;

        var type = Value(recurrence, Calendar, "Type");
        var frequency = type switch { "0" => "DAILY", "1" => "WEEKLY", "2" or "3" => "MONTHLY", "5" or "6" => "YEARLY",
            _ => throw new NotSupportedException("The EAS recurrence type is not supported.") };
        var parts = new List<string> { "FREQ=" + frequency };
        foreach (var (tag, key) in new[] { ("Interval", "INTERVAL"), ("Occurrences", "COUNT"), ("Until", "UNTIL"), ("DayOfMonth", "BYMONTHDAY"), ("MonthOfYear", "BYMONTH") })
        {
            var value = Value(recurrence, Calendar, tag);
            if (!string.IsNullOrEmpty(value))
                parts.Add(key + "=" + value);
        }

        if (int.TryParse(Value(recurrence, Calendar, "DayOfWeek"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
        {
            var days = new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
            var week = Value(recurrence, Calendar, "WeekOfMonth");
            var ordinal = type is "3" or "6" ? week == "5" ? "-1" : week : string.Empty;
            parts.Add("BYDAY=" + string.Join(',', days.Where((_, index) => (mask & (1 << index)) != 0).Select(day => ordinal + day)));
        }

        return "RRULE:" + string.Join(';', parts);
    }
}
