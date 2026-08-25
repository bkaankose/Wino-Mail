using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public class CalendarEventComposeNavigationArgs
{
    public Guid? SelectedCalendarId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public bool IsAllDay { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string NotesHtml { get; set; } = string.Empty;
    public List<CalendarEventAttendeeDraft> Attendees { get; set; } = [];
    public CalendarEventRecurrenceDraft? Recurrence { get; set; }
    public int? ReminderMinutesBeforeStart { get; set; }
    public CalendarItemShowAs? ShowAs { get; set; }
    public List<string> AccountAddressHints { get; set; } = [];
    public bool RequireCalendarPickerWhenUnresolved { get; set; }
    public bool HasUnsupportedImportContent { get; set; }
}

public sealed record CalendarEventAttendeeDraft(string Name, string Email);

public sealed class CalendarEventRecurrenceDraft
{
    public CalendarItemRecurrenceFrequency Frequency { get; init; }
    public int Interval { get; init; } = 1;
    public List<DayOfWeek> Weekdays { get; init; } = [];
    public DateTime? EndDate { get; init; }
}
