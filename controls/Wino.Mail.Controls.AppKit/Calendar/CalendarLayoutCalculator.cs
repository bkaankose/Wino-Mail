using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>One item rectangle on a calendar surface, in the surface's flipped coordinates.</summary>
public sealed record CalendarItemPlacement(ICalendarItem Item, DateOnly Date, CGRect Bounds, CalendarPlacementKind Kind);

public sealed record CalendarMonthCell(DateOnly Date, int Index, int Row, int Column, CGRect Bounds);

/// <summary>
/// AppKit adapter over the shared <see cref="CalendarSurfaceLayoutCalculator"/> (the port of the Windows
/// timed and month layout calculators): converts its neutral rectangles to <see cref="CGRect"/>.
/// </summary>
public static class CalendarLayoutCalculator
{
    public const int MonthColumns = CalendarSurfaceLayoutCalculator.MonthColumns;
    public const int MonthRows = CalendarSurfaceLayoutCalculator.MonthRows;

    public static double GetAllDayHeight(int laneCount) => CalendarSurfaceLayoutCalculator.GetAllDayHeight(laneCount);

    public static int GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
        => CalendarSurfaceLayoutCalculator.GetAllDayLaneCount(dates, items);

    /// <summary>Timed and multi-day placements for the hour grid. X is relative to the first day column.</summary>
    public static List<CalendarItemPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
        => CalendarSurfaceLayoutCalculator.CalculateTimed(dates, items, dayWidth, hourHeight, mode).ConvertAll(ToAppKit);

    /// <summary>All-day lanes above the hour grid.</summary>
    public static List<CalendarItemPlacement> CalculateAllDay(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth)
        => CalendarSurfaceLayoutCalculator.CalculateAllDay(dates, items, dayWidth).ConvertAll(ToAppKit);

    /// <summary>The 6x7 month grid. Dates must already be week-aligned (42 entries).</summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items) CalculateMonth(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double width, double height)
    {
        var (cells, placements) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, items, width, height);
        return (cells.ConvertAll(cell => new CalendarMonthCell(cell.Date, cell.Index, cell.Row, cell.Column, ToRect(cell.Bounds))), placements.ConvertAll(ToAppKit));
    }

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static IReadOnlyList<DateOnly> MonthGridDates(VisibleDateRange range, DayOfWeek firstDayOfWeek)
        => CalendarSurfaceLayoutCalculator.MonthGridDates(range.StartDate, firstDayOfWeek);

    private static CalendarItemPlacement ToAppKit(CalendarLayoutPlacement placement)
        => new(placement.Item, placement.Date, ToRect(placement.Bounds), placement.Kind);

    private static CGRect ToRect(CalendarLayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
