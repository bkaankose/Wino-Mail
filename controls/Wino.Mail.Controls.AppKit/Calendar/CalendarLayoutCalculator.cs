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
        => CalendarSpanLayout.GetAllDayLaneCount(dates, items);

    /// <summary>
    /// Timed and multi-day placements for the hour grid. X is relative to the first day column.
    /// In the stacked mode the shared layout keeps the Windows click gap to the right of a lone event only, so
    /// side-by-side events reached the column edge and a lone one stopped short of it; on the Mac every event
    /// ends at the same inset.
    /// </summary>
    public static List<CalendarItemPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
    {
        var placements = CalendarSurfaceLayoutCalculator.CalculateTimed(dates, items, dayWidth, hourHeight, mode).ConvertAll(ToAppKit);
        if (mode != CalendarEventDisplayMode.Stacked || dayWidth <= 0) return placements;
        for (int index = 0; index < placements.Count; index++)
        {
            var bounds = placements[index].Bounds;
            double dayRight = (Math.Floor((bounds.X + 0.5) / dayWidth) + 1) * dayWidth;
            if (Math.Abs(dayRight - 2 - CalendarSurfaceLayoutCalculator.ItemRightSpacing - bounds.GetMaxX()) < 0.5)
                placements[index] = placements[index] with { Bounds = new CGRect(bounds.X, bounds.Y, bounds.Width + CalendarSurfaceLayoutCalculator.ItemRightSpacing, bounds.Height) };
        }
        return placements;
    }

    /// <summary>
    /// All-day bars above the hour grid, and the days whose extra bars sit behind an "n more" link. The bar that
    /// crosses <paramref name="breakIndex"/> (the first day of the shown period) restarts there.
    /// </summary>
    public static (List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateAllDay(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth, int breakIndex = 0)
        => CalendarSpanLayout.CalculateAllDay(dates, items, dayWidth, breakIndex);

    /// <summary>The month grid (7 cells per week row). Dates must already be week-aligned.</summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateMonth(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double width, double height)
        => CalendarSpanLayout.CalculateMonth(dates, items, width, height);

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static IReadOnlyList<DateOnly> MonthGridDates(VisibleDateRange range, DayOfWeek firstDayOfWeek)
        => CalendarSurfaceLayoutCalculator.MonthGridDates(range.StartDate, firstDayOfWeek);

    private static CalendarItemPlacement ToAppKit(CalendarLayoutPlacement placement)
        => new(placement.Item, placement.Date, ToRect(placement.Bounds), placement.Kind);

    private static CGRect ToRect(CalendarLayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
