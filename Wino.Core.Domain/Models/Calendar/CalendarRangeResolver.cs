using System;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public static class CalendarRangeResolver
{
    public static VisibleDateRange Resolve(CalendarDisplayRequest request, CalendarSettings settings, DateOnly today)
    {
        var startDate = GetStartDate(request.DisplayType, request.AnchorDate, settings);
        var endDate = GetEndDate(request.DisplayType, request.AnchorDate, startDate, settings);
        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        var dates = Enumerable.Range(0, dayCount)
                              .Select(offset => startDate.AddDays(offset))
                              .ToArray();

        return new VisibleDateRange(
            request.DisplayType,
            request.AnchorDate,
            startDate,
            endDate,
            request.AnchorDate,
            dayCount,
            today >= startDate && today <= endDate,
            startDate.Year == endDate.Year && startDate.Month == endDate.Month,
            dates);
    }

    public static VisibleDateRange ChangeDisplayType(VisibleDateRange currentRange, CalendarDisplayType targetDisplayType, CalendarSettings settings, DateOnly today)
    {
        if (currentRange.DisplayType == targetDisplayType)
        {
            return currentRange;
        }

        var anchorDate = currentRange.AnchorDate;

        if (currentRange.DisplayType == CalendarDisplayType.Month)
        {
            anchorDate = currentRange.Contains(today) ? today : currentRange.StartDate;
        }

        return Resolve(new CalendarDisplayRequest(targetDisplayType, anchorDate), settings, today);
    }

    public static VisibleDateRange Navigate(VisibleDateRange currentRange, int direction, CalendarSettings settings, DateOnly today)
    {
        if (direction == 0)
        {
            return currentRange;
        }

        var normalizedDirection = Math.Sign(direction);
        var anchorDate = currentRange.DisplayType switch
        {
            CalendarDisplayType.Day => currentRange.AnchorDate.AddDays(normalizedDirection),
            CalendarDisplayType.Week => currentRange.AnchorDate.AddDays(7 * normalizedDirection),
            CalendarDisplayType.WorkWeek => currentRange.AnchorDate.AddDays(7 * normalizedDirection),
            CalendarDisplayType.Month => currentRange.AnchorDate.AddMonths(normalizedDirection),
            _ => currentRange.AnchorDate
        };

        return Resolve(new CalendarDisplayRequest(currentRange.DisplayType, anchorDate), settings, today);
    }

    private static DateOnly GetStartDate(CalendarDisplayType displayType, DateOnly anchorDate, CalendarSettings settings)
    {
        return displayType switch
        {
            CalendarDisplayType.Day => anchorDate,
            CalendarDisplayType.Week => GetStartOfWeek(anchorDate, settings.FirstDayOfWeek),
            CalendarDisplayType.WorkWeek => GetStartOfWorkWeek(anchorDate, settings),
            CalendarDisplayType.Month => new DateOnly(anchorDate.Year, anchorDate.Month, 1),
            _ => anchorDate
        };
    }

    private static DateOnly GetEndDate(CalendarDisplayType displayType, DateOnly anchorDate, DateOnly startDate, CalendarSettings settings)
    {
        return displayType switch
        {
            CalendarDisplayType.Day => anchorDate,
            CalendarDisplayType.Week => startDate.AddDays(6),
            CalendarDisplayType.WorkWeek => startDate.AddDays(settings.WorkWeekDayCount - 1),
            CalendarDisplayType.Month => new DateOnly(anchorDate.Year, anchorDate.Month, DateTime.DaysInMonth(anchorDate.Year, anchorDate.Month)),
            _ => anchorDate
        };
    }

    private static DateOnly GetStartOfWeek(DateOnly date, DayOfWeek firstDayOfWeek)
    {
        var offset = ((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return date.AddDays(-offset);
    }

    private static DateOnly GetStartOfWorkWeek(DateOnly anchorDate, CalendarSettings settings)
    {
        var startOfWeek = GetStartOfWeek(anchorDate, settings.FirstDayOfWeek);
        var offsetToWorkWeekStart = settings.GetWeekOffset(settings.WorkWeekStart);
        return startOfWeek.AddDays(offsetToWorkWeekStart);
    }
}
