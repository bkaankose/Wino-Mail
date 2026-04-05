using System;

namespace Wino.Core.Domain.Models.Calendar;

public class CalendarEventComposeNavigationArgs
{
    public Guid? SelectedCalendarId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public bool IsAllDay { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}
