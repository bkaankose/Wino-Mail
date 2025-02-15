namespace Wino.Core.Domain.Models.Calendar;

public class CalendarRenderOptions
{
    public CalendarRenderOptions(DateRange dateRange, CalendarSettings calendarSettings)
    {
        DateRange = dateRange;
        CalendarSettings = calendarSettings;
    }
    public int TotalDayCount => DateRange.TotalDays;
    public DateRange DateRange { get; }
    public CalendarSettings CalendarSettings { get; }
}
