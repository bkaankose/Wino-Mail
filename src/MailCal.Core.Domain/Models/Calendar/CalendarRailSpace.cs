#nullable enable
using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Calendar;

/// <summary>
/// The direction the calendar rail moves in for a display type.
/// </summary>
public enum CalendarRailAxis
{
    /// <summary>Units are columns laid out left to right (day, work week and week views).</summary>
    Horizontal,

    /// <summary>Units are rows laid out top to bottom (month view).</summary>
    Vertical
}

/// <summary>
/// The infinite strip of units a calendar surface pans over. Day and week views pan over day
/// columns, the work week view over work-day columns and the month view over week rows. A unit
/// index identifies one column or row; the surface shows <see cref="VisibleUnitCount"/> of them
/// starting at a snap unit. Every platform surface shares this model so that navigation, snapping
/// and the dates behind every unit are identical on Windows, macOS and iOS.
/// </summary>
public sealed class CalendarRailSpace
{
    private const int DaysPerWeek = 7;
    private const int MonthRowCount = 6;

    private readonly int _weekStartDayNumber;
    private readonly int _workWeekOffset;
    private readonly int _workWeekDayCount;

    private CalendarRailSpace(CalendarDisplayType displayType, CalendarSettings settings)
    {
        DisplayType = displayType;
        Settings = settings;
        Axis = displayType == CalendarDisplayType.Month ? CalendarRailAxis.Vertical : CalendarRailAxis.Horizontal;

        // Day number 0 (1 January 0001) is a Monday, so the first day number that falls on the
        // configured first day of the week aligns every week index.
        _weekStartDayNumber = ((int)settings.FirstDayOfWeek - (int)DayOfWeek.Monday + DaysPerWeek) % DaysPerWeek;
        _workWeekOffset = settings.GetWeekOffset(settings.WorkWeekStart);
        _workWeekDayCount = Math.Max(1, settings.WorkWeekDayCount);

        VisibleUnitCount = displayType switch
        {
            CalendarDisplayType.Day => 1,
            CalendarDisplayType.WorkWeek => _workWeekDayCount,
            CalendarDisplayType.Week => DaysPerWeek,
            CalendarDisplayType.Month => MonthRowCount,
            _ => 1
        };
    }

    public CalendarDisplayType DisplayType { get; }

    public CalendarSettings Settings { get; }

    public CalendarRailAxis Axis { get; }

    /// <summary>
    /// The number of units the viewport shows at once: 1 day, the work-week day count, 7 days or 6 week rows.
    /// </summary>
    public int VisibleUnitCount { get; }

    /// <summary>
    /// The number of dates a single unit covers: one for columns, seven for month rows.
    /// </summary>
    public int DatesPerUnit => Axis == CalendarRailAxis.Vertical ? DaysPerWeek : 1;

    public static CalendarRailSpace Create(CalendarDisplayType displayType, CalendarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new CalendarRailSpace(displayType, settings);
    }

    /// <summary>
    /// Whether this space produces the same units as another one. Changing the display type or
    /// the week settings rebuilds the rail; everything else keeps it.
    /// </summary>
    public bool HasSameUnits(CalendarRailSpace other)
        => other.DisplayType == DisplayType &&
           other._weekStartDayNumber == _weekStartDayNumber &&
           other._workWeekOffset == _workWeekOffset &&
           other._workWeekDayCount == _workWeekDayCount;

    /// <summary>
    /// The first date of a unit: the column's date, or the first day of the week row.
    /// </summary>
    public DateOnly GetUnitStartDate(int unit)
    {
        return DisplayType switch
        {
            CalendarDisplayType.WorkWeek => DateOnly.FromDayNumber(GetWorkDayNumber(unit)),
            CalendarDisplayType.Month => DateOnly.FromDayNumber(GetWeekStartDayNumber(unit)),
            _ => DateOnly.FromDayNumber(unit)
        };
    }

    /// <summary>
    /// Every date a unit covers, in order.
    /// </summary>
    public IReadOnlyList<DateOnly> GetUnitDates(int unit)
    {
        var start = GetUnitStartDate(unit);

        if (Axis == CalendarRailAxis.Horizontal)
        {
            return new[] { start };
        }

        var dates = new DateOnly[DaysPerWeek];

        for (var index = 0; index < DaysPerWeek; index++)
        {
            dates[index] = start.AddDays(index);
        }

        return dates;
    }

    /// <summary>
    /// Every date the viewport shows when its first unit is <paramref name="firstUnit"/>.
    /// </summary>
    public IReadOnlyList<DateOnly> GetViewportDates(int firstUnit)
    {
        var dates = new List<DateOnly>(VisibleUnitCount * DatesPerUnit);

        for (var unit = firstUnit; unit < firstUnit + VisibleUnitCount; unit++)
        {
            dates.AddRange(GetUnitDates(unit));
        }

        return dates;
    }

    /// <summary>
    /// The unit that contains a date. Dates outside the work week map to the nearest work day.
    /// </summary>
    public int GetUnitIndex(DateOnly date)
    {
        switch (DisplayType)
        {
            case CalendarDisplayType.Month:
                return GetWeekIndex(date.DayNumber);
            case CalendarDisplayType.WorkWeek:
                return GetWorkDayUnit(date.DayNumber);
            default:
                return date.DayNumber;
        }
    }

    /// <summary>
    /// Whether the viewport may come to rest with this unit in its first position.
    /// </summary>
    public bool IsSnapUnit(int unit)
    {
        switch (DisplayType)
        {
            case CalendarDisplayType.Day:
                return true;
            case CalendarDisplayType.Week:
                return (unit - _weekStartDayNumber) % DaysPerWeek == 0;
            case CalendarDisplayType.WorkWeek:
                return Mod(unit, _workWeekDayCount) == 0;
            case CalendarDisplayType.Month:
                return RowContainsFirstOfMonth(unit);
            default:
                return true;
        }
    }

    public int SnapAtOrBefore(int unit)
    {
        while (!IsSnapUnit(unit))
        {
            unit--;
        }

        return unit;
    }

    public int SnapAtOrAfter(int unit)
    {
        while (!IsSnapUnit(unit))
        {
            unit++;
        }

        return unit;
    }

    /// <summary>
    /// The first unit of the period that shows <paramref name="anchorDate"/>, matching
    /// <see cref="CalendarRangeResolver.Resolve"/> for the same anchor.
    /// </summary>
    public int GetPeriodStartUnit(DateOnly anchorDate)
    {
        switch (DisplayType)
        {
            case CalendarDisplayType.Month:
                return GetWeekIndex(new DateOnly(anchorDate.Year, anchorDate.Month, 1).DayNumber);
            case CalendarDisplayType.WorkWeek:
                return GetWeekIndex(anchorDate.DayNumber) * _workWeekDayCount;
            case CalendarDisplayType.Week:
                return SnapAtOrBefore(anchorDate.DayNumber);
            default:
                return anchorDate.DayNumber;
        }
    }

    /// <summary>
    /// The first unit of the previous or next period relative to a snap unit. The arrow buttons
    /// and the keyboard move by whole periods; the month view moves to the next month even when
    /// it rests on an arbitrary week row.
    /// </summary>
    public int MovePeriod(int snapUnit, int direction)
    {
        if (direction == 0)
        {
            return snapUnit;
        }

        var step = Math.Sign(direction);

        switch (DisplayType)
        {
            case CalendarDisplayType.Day:
                return snapUnit + step;
            case CalendarDisplayType.Week:
                return snapUnit + (DaysPerWeek * step);
            case CalendarDisplayType.WorkWeek:
                return snapUnit + (_workWeekDayCount * step);
            default:
                return step > 0
                    ? SnapAtOrAfter(snapUnit + 1)
                    : SnapAtOrBefore(snapUnit - 1);
        }
    }

    /// <summary>
    /// The anchor date the view model resolves for a snap unit. Resolving it with
    /// <see cref="CalendarRangeResolver.Resolve"/> yields a range that starts on that unit.
    /// </summary>
    public DateOnly GetAnchorDate(int snapUnit)
    {
        if (DisplayType != CalendarDisplayType.Month)
        {
            return GetUnitStartDate(snapUnit);
        }

        foreach (var date in GetUnitDates(snapUnit))
        {
            if (date.Day == 1)
            {
                return date;
            }
        }

        return GetDominantMonth(snapUnit);
    }

    /// <summary>
    /// The first day of the month that owns most of the viewport starting at <paramref name="firstUnit"/>.
    /// Month cells outside this month render dimmed, and the title names it while the rail moves.
    /// </summary>
    public DateOnly GetDominantMonth(int firstUnit)
    {
        var counts = new Dictionary<(int Year, int Month), int>();
        var best = default((int Year, int Month));
        var bestCount = -1;

        foreach (var date in GetViewportDates(firstUnit))
        {
            var key = (date.Year, date.Month);
            var count = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
            counts[key] = count;

            if (count > bestCount || (count == bestCount && key.CompareTo(best) < 0))
            {
                best = key;
                bestCount = count;
            }
        }

        return bestCount < 0 ? GetUnitStartDate(firstUnit) : new DateOnly(best.Year, best.Month, 1);
    }

    /// <summary>
    /// The visible range the view model should load for a snap unit.
    /// </summary>
    public VisibleDateRange ToVisibleRange(int snapUnit, DateOnly today)
        => CalendarRangeResolver.Resolve(new CalendarDisplayRequest(DisplayType, GetAnchorDate(snapUnit)), Settings, today);

    private int GetWeekStartDayNumber(int weekIndex) => _weekStartDayNumber + (weekIndex * DaysPerWeek);

    private int GetWeekIndex(int dayNumber) => FloorDivide(dayNumber - _weekStartDayNumber, DaysPerWeek);

    private int GetWorkDayNumber(int unit)
    {
        var weekIndex = FloorDivide(unit, _workWeekDayCount);
        var dayOffset = Mod(unit, _workWeekDayCount);

        return GetWeekStartDayNumber(weekIndex) + _workWeekOffset + dayOffset;
    }

    private int GetWorkDayUnit(int dayNumber)
    {
        // A work week can run past its calendar week (a Friday start with five days ends on Tuesday),
        // so the previous week's work days are checked before this week's.
        var weekIndex = GetWeekIndex(dayNumber);

        for (var candidate = weekIndex; candidate >= weekIndex - 1; candidate--)
        {
            var firstWorkDay = GetWeekStartDayNumber(candidate) + _workWeekOffset;
            var lastWorkDay = firstWorkDay + _workWeekDayCount - 1;

            if (dayNumber >= firstWorkDay && dayNumber <= lastWorkDay)
            {
                return (candidate * _workWeekDayCount) + (dayNumber - firstWorkDay);
            }
        }

        var thisWeekFirstWorkDay = GetWeekStartDayNumber(weekIndex) + _workWeekOffset;

        return dayNumber < thisWeekFirstWorkDay
            ? (weekIndex * _workWeekDayCount) - 1
            : (weekIndex * _workWeekDayCount) + _workWeekDayCount - 1;
    }

    private bool RowContainsFirstOfMonth(int weekIndex)
    {
        var start = GetWeekStartDayNumber(weekIndex);

        for (var offset = 0; offset < DaysPerWeek; offset++)
        {
            if (DateOnly.FromDayNumber(start + offset).Day == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;

        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }

    private static int Mod(int value, int divisor)
    {
        var remainder = value % divisor;

        return remainder < 0 ? remainder + divisor : remainder;
    }
}
