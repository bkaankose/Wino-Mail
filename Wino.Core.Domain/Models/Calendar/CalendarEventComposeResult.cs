using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public class CalendarEventComposeResult
{
    public Guid CalendarId { get; set; }
    public Guid AccountId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string HtmlNotes { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsAllDay { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public CalendarItemShowAs ShowAs { get; set; }
    public List<Reminder> SelectedReminders { get; set; } = [];
    public List<CalendarEventAttendee> Attendees { get; set; } = [];
    public List<CalendarEventComposeAttachmentDraft> Attachments { get; set; } = [];
    public string Recurrence { get; set; } = string.Empty;
    public string RecurrenceSummary { get; set; } = string.Empty;
}
