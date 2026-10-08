using System;
using System.Collections.Generic;
using System.Linq;
using Itenso.TimePeriod;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public sealed record VisibleDateRange(
    CalendarDisplayType DisplayType,
    DateOnly AnchorDate,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly PrimaryDate,
    int DayCount,
    bool ContainsToday,
    bool SpansSingleMonth,
    IReadOnlyList<DateOnly> Dates)
{
    public DateRange ToDateRangeExclusive()
        => new(StartDate.ToDateTime(TimeOnly.MinValue), EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));

    public ITimePeriod ToTimePeriod()
        => new TimeRange(StartDate.ToDateTime(TimeOnly.MinValue), EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));

    public bool Contains(DateOnly date)
        => date >= StartDate && date <= EndDate;

    public bool Contains(DateTime date)
        => Contains(DateOnly.FromDateTime(date));

    public static VisibleDateRange FromDateRange(CalendarDisplayType displayType, DateRange dateRange, DateOnly anchorDate, DateOnly today)
    {
        var startDate = DateOnly.FromDateTime(dateRange.StartDate);
        var endDate = DateOnly.FromDateTime(dateRange.EndDate.AddDays(-1));
        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        var dates = Enumerable.Range(0, dayCount)
                              .Select(offset => startDate.AddDays(offset))
                              .ToArray();

        return new VisibleDateRange(
            displayType,
            anchorDate,
            startDate,
            endDate,
            anchorDate,
            dayCount,
            today >= startDate && today <= endDate,
            startDate.Year == endDate.Year && startDate.Month == endDate.Month,
            dates);
    }
}
