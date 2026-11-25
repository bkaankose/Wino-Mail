using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The shared <see cref="CalendarSurfaceLayoutCalculator"/> (timed) and <see cref="CalendarSpanLayout"/> (all-day and month
/// bars) in rail coordinates: timed and all-day x include the hour gutter, timed y the timeline padding.
/// </summary>
internal static class CalendarLayoutAdapter
{
    public static double GetAllDayHeight(int laneCount, bool hasMoreRow) => CalendarSurfaceLayoutCalculator.GetAllDayHeight(laneCount, hasMoreRow);

    public static (int Lanes, bool HasOverflow) GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
        => CalendarSpanLayout.GetAllDayLaneCount(dates, items);

    /// <summary>
    /// In the stacked mode the shared layout keeps the Windows click gap to the right of a lone event only; as on the Mac
    /// every event ends at the same inset.
    /// </summary>
    public static List<CalendarLayoutPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
    {
        var placements = CalendarSurfaceLayoutCalculator.CalculateTimed(dates, items, dayWidth, hourHeight, mode);
        for (var index = 0; index < placements.Count; index++)
        {
            var bounds = placements[index].Bounds;
            if (mode == CalendarEventDisplayMode.Stacked && dayWidth > 0)
            {
                var dayRight = (Math.Floor((bounds.X + 0.5) / dayWidth) + 1) * dayWidth;
                if (Math.Abs(dayRight - 2 - CalendarSurfaceLayoutCalculator.ItemRightSpacing - bounds.Right) < 0.5)
                    bounds = bounds with { Width = bounds.Width + CalendarSurfaceLayoutCalculator.ItemRightSpacing };
            }
            placements[index] = placements[index] with { Bounds = bounds.Offset(WinoCalendarSurfaceGeometry.HourColumnWidth, WinoCalendarSurfaceGeometry.TimelinePadding) };
        }
        return placements;
    }

    public static (List<CalendarLayoutPlacement> Items, List<CalendarLayoutOverflowCell> Overflows) CalculateAllDay(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double dayWidth, int breakIndex)
    {
        var (placements, overflows) = CalendarSpanLayout.CalculateAllDay(dates, items, dayWidth, breakIndex);
        for (var index = 0; index < placements.Count; index++)
            placements[index] = placements[index] with { Bounds = placements[index].Bounds.Offset(WinoCalendarSurfaceGeometry.HourColumnWidth, 0) };
        for (var index = 0; index < overflows.Count; index++)
            overflows[index] = overflows[index] with { Bounds = overflows[index].Bounds.Offset(WinoCalendarSurfaceGeometry.HourColumnWidth, 0) };
        return (placements, overflows);
    }

    public static (List<CalendarLayoutMonthCell> Cells, List<CalendarLayoutPlacement> Items, List<CalendarLayoutOverflowCell> Overflows) CalculateMonth(
        IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double width, double height)
        => CalendarSpanLayout.CalculateMonth(dates, items, width, height);
}
