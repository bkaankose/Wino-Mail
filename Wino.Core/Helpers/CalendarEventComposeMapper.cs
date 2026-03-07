using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Helpers;

public sealed record PreparedCalendarEventCreateModel(
    CalendarItem CalendarItem,
    List<CalendarEventAttendee> Attendees,
    List<Reminder> Reminders);

public static class CalendarEventComposeMapper
{
    public static PreparedCalendarEventCreateModel Prepare(CalendarEventComposeResult composeResult, AccountCalendar assignedCalendar, Guid? calendarItemId = null)
    {
        ArgumentNullException.ThrowIfNull(composeResult);
        ArgumentNullException.ThrowIfNull(assignedCalendar);

        var itemId = calendarItemId ?? Guid.NewGuid();
        var effectiveTimeZoneId = string.IsNullOrWhiteSpace(composeResult.TimeZoneId)
            ? TimeZoneInfo.Local.Id
            : composeResult.TimeZoneId;
        var utcNow = DateTimeOffset.UtcNow;

        var calendarItem = new CalendarItem
        {
            Id = itemId,
            CalendarId = assignedCalendar.Id,
            AssignedCalendar = assignedCalendar,
            Title = composeResult.Title?.Trim() ?? string.Empty,
            Description = composeResult.HtmlNotes ?? string.Empty,
            Location = composeResult.Location?.Trim() ?? string.Empty,
            StartDate = composeResult.StartDate,
            DurationInSeconds = Math.Max(0, (composeResult.EndDate - composeResult.StartDate).TotalSeconds),
            StartTimeZone = effectiveTimeZoneId,
            EndTimeZone = effectiveTimeZoneId,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
            Recurrence = composeResult.Recurrence ?? string.Empty,
            OrganizerDisplayName = assignedCalendar.MailAccount?.SenderName ?? string.Empty,
            OrganizerEmail = assignedCalendar.MailAccount?.Address ?? string.Empty,
            Status = CalendarItemStatus.Accepted,
            Visibility = CalendarItemVisibility.Public,
            ShowAs = composeResult.ShowAs,
            IsHidden = false,
            IsLocked = false
        };

        var attendees = composeResult.Attendees?
            .Where(attendee => attendee != null)
            .Select(attendee => new CalendarEventAttendee
            {
                Id = attendee.Id == Guid.Empty ? Guid.NewGuid() : attendee.Id,
                CalendarItemId = itemId,
                Name = attendee.Name ?? string.Empty,
                Email = attendee.Email ?? string.Empty,
                Comment = attendee.Comment,
                AttendenceStatus = attendee.AttendenceStatus,
                IsOrganizer = attendee.IsOrganizer,
                IsOptionalAttendee = attendee.IsOptionalAttendee,
                ResolvedContact = attendee.ResolvedContact
            })
            .ToList() ?? [];

        var reminders = composeResult.SelectedReminders?
            .Where(reminder => reminder != null)
            .Select(reminder => new Reminder
            {
                Id = reminder.Id == Guid.Empty ? Guid.NewGuid() : reminder.Id,
                CalendarItemId = itemId,
                DurationInSeconds = reminder.DurationInSeconds,
                ReminderType = reminder.ReminderType
            })
            .ToList() ?? [];

        return new PreparedCalendarEventCreateModel(calendarItem, attendees, reminders);
    }
}
