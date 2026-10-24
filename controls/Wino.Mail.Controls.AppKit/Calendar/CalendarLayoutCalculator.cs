using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>One item rectangle on a calendar surface, in the surface's flipped coordinates.</summary>
public sealed record CalendarItemPlacement(ICalendarItem Item, DateOnly Date, CGRect Bounds, CalendarPlacementKind Kind);

public sealed record CalendarMonthCell(DateOnly Date, int Index, int Row, int Column, CGRect Bounds);

/// <summary>The "n more events" link of a day whose items did not all fit, with the hidden items.</summary>
public sealed record CalendarOverflowCell(DateOnly Date, List<ICalendarItem> HiddenItems, CGRect Bounds)
{
    public string Text => CalendarItemOverflow.FormatMoreText(HiddenItems.Count);
}

/// <summary>
/// AppKit adapter over the shared <see cref="CalendarSurfaceLayoutCalculator"/> (the port of the Windows
/// timed and month layout calculators): converts its neutral rectangles to <see cref="CGRect"/>.
/// </summary>
public static class CalendarLayoutCalculator
{
    public const int MonthColumns = CalendarSurfaceLayoutCalculator.MonthColumns;
    public const int MonthRows = CalendarSurfaceLayoutCalculator.MonthRows;

    public static double GetAllDayHeight(int laneCount, bool hasMoreRow) => CalendarSurfaceLayoutCalculator.GetAllDayHeight(laneCount, hasMoreRow);

    public static (int Lanes, bool HasOverflow) GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
        => CalendarSurfaceLayoutCalculator.GetAllDayLaneCount(dates, items);

    /// <summary>Timed and multi-day placements for the hour grid. X is relative to the first day column.</summary>
    public static List<CalendarItemPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
        => CalendarSurfaceLayoutCalculator.CalculateTimed(dates, items, dayWidth, hourHeight, mode).ConvertAll(ToAppKit);

    /// <summary>All-day lanes above the hour grid, and the days whose extra items sit behind an "n more" link.</summary>
    public static (List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateAllDay(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth)
    {
        var (items2, overflows) = CalendarSurfaceLayoutCalculator.CalculateAllDay(dates, items, dayWidth);
        return (items2.ConvertAll(ToAppKit), overflows.ConvertAll(ToAppKit));
    }

    /// <summary>The month grid (7 cells per week row). Dates must already be week-aligned.</summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateMonth(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double width, double height)
    {
        var (cells, placements, overflows) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, items, width, height);
        return (cells.ConvertAll(cell => new CalendarMonthCell(cell.Date, cell.Index, cell.Row, cell.Column, ToRect(cell.Bounds))),
            placements.ConvertAll(ToAppKit), overflows.ConvertAll(ToAppKit));
    }

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static IReadOnlyList<DateOnly> MonthGridDates(VisibleDateRange range, DayOfWeek firstDayOfWeek)
        => CalendarSurfaceLayoutCalculator.MonthGridDates(range.StartDate, firstDayOfWeek);

    private static CalendarItemPlacement ToAppKit(CalendarLayoutPlacement placement)
        => new(placement.Item, placement.Date, ToRect(placement.Bounds), placement.Kind);

    private static CalendarOverflowCell ToAppKit(CalendarLayoutOverflowCell overflow)
        => new(overflow.Date, overflow.HiddenItems, ToRect(overflow.Bounds));

    private static CGRect ToRect(CalendarLayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
