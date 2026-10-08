namespace Wino.Core.Domain.Models.Calendar;

public interface ICalendarRangeTextFormatter
{
    string Format(VisibleDateRange range, IDateContextProvider dateContextProvider);
}
