using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>Where a placement lives; mirrors the Windows CalendarItemControl visual states.</summary>
public enum CalendarPlacementKind
{
    /// <summary>A timed event in the hour grid.</summary>
    Timed,
    /// <summary>An all-day event in the all-day strip (32pt, title only).</summary>
    AllDay,
    /// <summary>A multi-day timed event's faint ghost in the hour grid (not hit-testable).</summary>
    MultiDayGhost,
    /// <summary>A month cell entry (title only).</summary>
    MonthCell
}

/// <summary>One item rectangle on a calendar surface, in the surface's flipped coordinates.</summary>
public sealed record CalendarItemPlacement(ICalendarItem Item, DateOnly Date, CGRect Bounds, CalendarPlacementKind Kind);

public sealed record CalendarMonthCell(DateOnly Date, int Index, int Row, int Column, CGRect Bounds);

/// <summary>
/// Ports the Windows TimedCalendarLayoutCalculator and MonthCalendarLayoutCalculator so the Mac
/// surface places events exactly like the Windows CalendarPeriodControl (same clusters, column
/// splitting, all-day lanes and month cell packing).
/// </summary>
public static class CalendarLayoutCalculator
{
    public const double ItemRightSpacing = CalendarEventPlacementCalculator.RightSpacing;
    public const double AllDayItemHeight = 32;
    public const double AllDayItemGap = 4;
    public const double AllDaySectionPadding = 6;
    public const int MonthColumns = 7;
    public const int MonthRows = 6;
    private const double MonthCellPadding = 4;
    private const double MonthDayLabelHeight = 20;
    private const double MonthRegularItemHeight = 18;
    private const double MonthExpandedItemHeight = 30;
    private const double MonthItemGap = 2;

    public static double GetAllDayHeight(int laneCount)
        => laneCount <= 0 ? 0 : AllDaySectionPadding * 2 + laneCount * AllDayItemHeight + (laneCount - 1) * AllDayItemGap;

    public static int GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
    {
        int lanes = 0;
        foreach (var date in dates) lanes = Math.Max(lanes, BuildAllDayItems(items, date).Count);
        return lanes;
    }

    /// <summary>Timed and multi-day placements for the hour grid. X is relative to the first day column.</summary>
    public static List<CalendarItemPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
    {
        var result = new List<CalendarItemPlacement>();
        var list = items as IList<ICalendarItem> ?? items.ToList();
        for (int dayIndex = 0; dayIndex < dates.Count; dayIndex++)
        {
            var date = dates[dayIndex];
            var segments = BuildDaySegments(list, date).OrderBy(s => s.StartMinute).ThenBy(s => s.EndMinute).ToList();
            double dayX = dayIndex * dayWidth;

            if (mode is CalendarEventDisplayMode.Overlapped or CalendarEventDisplayMode.LimitedOverlap or CalendarEventDisplayMode.ProtectTitles)
            {
                var placements = CalendarEventPlacementCalculator.Calculate(
                    segments.Select((s, index) => new CalendarOverlapInterval(index, s.Item.Id, s.StartMinute, s.EndMinute)), dayWidth, hourHeight, mode);
                foreach (var placement in placements)
                {
                    var segment = segments[placement.SourceIndex];
                    double y = segment.StartMinute / 60 * hourHeight;
                    double height = Math.Max(1, (segment.EndMinute - segment.StartMinute) / 60 * hourHeight);
                    result.Add(new CalendarItemPlacement(segment.Item, date, new CGRect(dayX + placement.Left, y, placement.Width, height), Kind(segment.Item)));
                }
                continue;
            }

            foreach (var cluster in BuildClusters(segments))
            {
                AssignColumns(cluster);
                int columns = cluster.Max(s => s.Column) + 1;
                double subWidth = dayWidth / columns;
                foreach (var segment in cluster)
                {
                    double x = dayX + segment.Column * subWidth + 2;
                    double width = Math.Max(0, subWidth - 4 - (columns == 1 ? ItemRightSpacing : 0));
                    double y = segment.StartMinute / 60 * hourHeight;
                    double height = Math.Max(1, (segment.EndMinute - segment.StartMinute) / 60 * hourHeight);
                    result.Add(new CalendarItemPlacement(segment.Item, date, new CGRect(x, y, width, height), Kind(segment.Item)));
                }
            }
        }
        return result;
    }

    private static CalendarPlacementKind Kind(ICalendarItem item) => item.IsMultiDayEvent ? CalendarPlacementKind.MultiDayGhost : CalendarPlacementKind.Timed;

    /// <summary>All-day lanes above the hour grid.</summary>
    public static List<CalendarItemPlacement> CalculateAllDay(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth)
    {
        var result = new List<CalendarItemPlacement>();
        var list = items as IList<ICalendarItem> ?? items.ToList();
        for (int dayIndex = 0; dayIndex < dates.Count; dayIndex++)
        {
            var date = dates[dayIndex];
            var dayItems = BuildAllDayItems(list, date).OrderBy(i => i.StartDate).ThenBy(i => i.EndDate).ThenBy(i => i.Title).ToList();
            for (int row = 0; row < dayItems.Count; row++)
            {
                double y = AllDaySectionPadding + row * (AllDayItemHeight + AllDayItemGap);
                result.Add(new CalendarItemPlacement(dayItems[row], date,
                    new CGRect(dayIndex * dayWidth + 2, y, Math.Max(0, dayWidth - 4), AllDayItemHeight), CalendarPlacementKind.AllDay));
            }
        }
        return result;
    }

    /// <summary>The 6x7 month grid. Dates must already be week-aligned (42 entries).</summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items) CalculateMonth(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double width, double height)
    {
        double cellWidth = width <= 0 ? 0 : width / MonthColumns;
        double cellHeight = height <= 0 ? 0 : height / MonthRows;
        var cells = new List<CalendarMonthCell>(dates.Count);
        for (int index = 0; index < dates.Count; index++)
        {
            int row = index / MonthColumns, column = index % MonthColumns;
            cells.Add(new CalendarMonthCell(dates[index], index, row, column, new CGRect(column * cellWidth, row * cellHeight, cellWidth, cellHeight)));
        }

        var placements = new List<CalendarItemPlacement>();
        var list = items as IList<ICalendarItem> ?? items.ToList();
        foreach (var cell in cells)
        {
            double nextY = cell.Bounds.Y + MonthDayLabelHeight + MonthCellPadding;
            foreach (var item in GetCellItems(list, cell.Date))
            {
                double itemHeight = item.IsAllDayEvent || item.IsMultiDayEvent ? MonthExpandedItemHeight : MonthRegularItemHeight;
                if (nextY + itemHeight > cell.Bounds.GetMaxY() - MonthCellPadding) break;
                placements.Add(new CalendarItemPlacement(item, cell.Date,
                    new CGRect(cell.Bounds.X + MonthCellPadding, nextY, Math.Max(0, cell.Bounds.Width - MonthCellPadding * 2), itemHeight), CalendarPlacementKind.MonthCell));
                nextY += itemHeight + MonthItemGap;
            }
        }
        return (cells, placements);
    }

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static IReadOnlyList<DateOnly> MonthGridDates(VisibleDateRange range, DayOfWeek firstDayOfWeek)
    {
        int offset = ((int)range.StartDate.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = range.StartDate.AddDays(-offset);
        return Enumerable.Range(0, MonthColumns * MonthRows).Select(i => start.AddDays(i)).ToArray();
    }

    private static IEnumerable<ICalendarItem> GetCellItems(IList<ICalendarItem> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        foreach (var item in items)
        {
            var start = item.StartDate; var end = item.EndDate;
            if (end <= start) continue;
            if (start < dayEnd && end > dayStart) yield return item;
        }
    }

    private static List<ICalendarItem> BuildAllDayItems(IEnumerable<ICalendarItem> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var result = new List<ICalendarItem>();
        foreach (var item in items)
        {
            if (!item.IsAllDayEvent) continue;
            var start = item.StartDate; var end = item.EndDate;
            if (end <= start) continue;
            if (start < dayEnd && end > dayStart) result.Add(item);
        }
        return result;
    }

    private static List<Segment> BuildDaySegments(IEnumerable<ICalendarItem> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var segments = new List<Segment>();
        foreach (var item in items)
        {
            if (item.IsAllDayEvent) continue;
            var start = item.StartDate; var end = item.EndDate;
            if (end <= start) continue;
            var segmentStart = start > dayStart ? start : dayStart;
            var segmentEnd = end < dayEnd ? end : dayEnd;
            if (segmentEnd <= segmentStart) continue;
            segments.Add(new Segment(item, (segmentStart - dayStart).TotalMinutes, (segmentEnd - dayStart).TotalMinutes));
        }
        return segments;
    }

    private static IEnumerable<List<Segment>> BuildClusters(List<Segment> segments)
    {
        if (segments.Count == 0) yield break;
        var cluster = new List<Segment> { segments[0] };
        double clusterEnd = segments[0].EndMinute;
        for (int index = 1; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.StartMinute < clusterEnd)
            {
                cluster.Add(segment);
                clusterEnd = Math.Max(clusterEnd, segment.EndMinute);
                continue;
            }
            yield return cluster;
            cluster = [segment];
            clusterEnd = segment.EndMinute;
        }
        yield return cluster;
    }

    private static void AssignColumns(List<Segment> segments)
    {
        var columnEnds = new List<double>();
        foreach (var segment in segments)
        {
            int assigned = -1;
            for (int column = 0; column < columnEnds.Count; column++)
            {
                if (columnEnds[column] <= segment.StartMinute) { assigned = column; columnEnds[column] = segment.EndMinute; break; }
            }
            if (assigned < 0) { assigned = columnEnds.Count; columnEnds.Add(segment.EndMinute); }
            segment.Column = assigned;
        }
    }

    private sealed class Segment(ICalendarItem item, double startMinute, double endMinute)
    {
        public ICalendarItem Item { get; } = item;
        public double StartMinute { get; } = startMinute;
        public double EndMinute { get; } = endMinute;
        public int Column { get; set; }
    }
}
