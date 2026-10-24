#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>A platform-neutral rectangle in a calendar surface's top-down coordinates.</summary>
public readonly record struct CalendarLayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public CalendarLayoutRect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

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

/// <summary>One item rectangle on a calendar surface.</summary>
public sealed record CalendarLayoutPlacement(ICalendarItem Item, DateOnly Date, CalendarLayoutRect Bounds, CalendarPlacementKind Kind);

/// <summary>One day cell of the month grid.</summary>
public sealed record CalendarLayoutMonthCell(DateOnly Date, int Index, int Row, int Column, CalendarLayoutRect Bounds);

/// <summary>
/// The "n more events" link of a day whose items did not all fit (Windows CalendarDayOverflow): the hidden items in
/// their stacking order and the link's rectangle, in the same coordinates as the placements around it.
/// </summary>
public sealed record CalendarLayoutOverflowCell(DateOnly Date, List<ICalendarItem> HiddenItems, CalendarLayoutRect Bounds)
{
    public string Text => CalendarItemOverflow.FormatMoreText(HiddenItems.Count);
}

/// <summary>
/// Event placement for native calendar surfaces, ported from the Windows TimedCalendarLayoutCalculator
/// and MonthCalendarLayoutCalculator: the same clusters, column splitting, all-day lanes and month
/// cell packing. Timed X values are relative to the first day column.
/// </summary>
public static class CalendarSurfaceLayoutCalculator
{
    public const double ItemRightSpacing = CalendarEventPlacementCalculator.RightSpacing;
    public const double AllDayItemHeight = 32;
    public const double AllDayItemGap = 4;
    public const double AllDaySectionPadding = 6;
    public const int MonthColumns = 7;
    public const int MonthRows = 6;
    public const double MonthCellPadding = 4;
    public const double MonthDayLabelHeight = 20;
    /// <summary>22 rather than the 21 a title line needs keeps descenders inside the tile (Windows MonthCalendarLayout).</summary>
    public const double MonthItemHeight = 22;
    public const double MonthItemGap = 2;

    /// <summary>The all-day strip height for <paramref name="laneCount"/> lanes, plus the "n more" row when a day overflows.</summary>
    public static double GetAllDayHeight(int laneCount, bool hasMoreRow)
    {
        if (laneCount <= 0) return 0;
        double height = AllDaySectionPadding * 2 + laneCount * AllDayItemHeight + (laneCount - 1) * AllDayItemGap;
        return hasMoreRow ? height + CalendarItemOverflow.MoreButtonGap + CalendarItemOverflow.MoreButtonHeight : height;
    }

    /// <summary>The lanes the strip shows (at most <see cref="CalendarItemOverflow.MaxAllDayLanes"/>) and whether any day has more.</summary>
    public static (int Lanes, bool HasOverflow) GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(items);

        var list = items as IList<ICalendarItem> ?? items.ToList();
        int count = 0;
        foreach (var date in dates) count = Math.Max(count, BuildAllDayItems(list, date).Count);
        return (Math.Min(count, CalendarItemOverflow.MaxAllDayLanes), count > CalendarItemOverflow.MaxAllDayLanes);
    }

    /// <summary>The Y of the all-day "n more" row: under the last lane the strip can show.</summary>
    public static double AllDayMoreRowY
        => AllDaySectionPadding + CalendarItemOverflow.MaxAllDayLanes * AllDayItemHeight +
           (CalendarItemOverflow.MaxAllDayLanes - 1) * AllDayItemGap + CalendarItemOverflow.MoreButtonGap;

    /// <summary>Timed and multi-day placements for the hour grid.</summary>
    public static List<CalendarLayoutPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(items);

        var result = new List<CalendarLayoutPlacement>();
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
                    result.Add(new CalendarLayoutPlacement(segment.Item, date, new CalendarLayoutRect(dayX + placement.Left, y, placement.Width, height), TimedKind(segment.Item)));
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
                    result.Add(new CalendarLayoutPlacement(segment.Item, date, new CalendarLayoutRect(x, y, width, height), TimedKind(segment.Item)));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// All-day lanes above the hour grid: at most <see cref="CalendarItemOverflow.MaxAllDayLanes"/> per day, the rest
    /// behind that day's "n more" link (Windows TimedCalendarLayoutCalculator.CalculateAllDayItems).
    /// </summary>
    public static (List<CalendarLayoutPlacement> Items, List<CalendarLayoutOverflowCell> Overflows) CalculateAllDay(
        IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(items);

        var result = new List<CalendarLayoutPlacement>();
        var overflows = new List<CalendarLayoutOverflowCell>();
        var list = items as IList<ICalendarItem> ?? items.ToList();
        const int maxLanes = CalendarItemOverflow.MaxAllDayLanes;
        double moreY = AllDayMoreRowY;
        for (int dayIndex = 0; dayIndex < dates.Count; dayIndex++)
        {
            var date = dates[dayIndex];
            var dayItems = BuildAllDayItems(list, date).OrderBy(i => i.StartDate).ThenBy(i => i.EndDate).ThenBy(i => i.Title).ToList();
            int lanes = Math.Min(dayItems.Count, maxLanes);
            for (int row = 0; row < lanes; row++)
            {
                double y = AllDaySectionPadding + row * (AllDayItemHeight + AllDayItemGap);
                result.Add(new CalendarLayoutPlacement(dayItems[row], date,
                    new CalendarLayoutRect(dayIndex * dayWidth + 2, y, Math.Max(0, dayWidth - 4), AllDayItemHeight), CalendarPlacementKind.AllDay));
            }

            if (lanes < dayItems.Count)
            {
                overflows.Add(new CalendarLayoutOverflowCell(date, dayItems.GetRange(lanes, dayItems.Count - lanes),
                    new CalendarLayoutRect(dayIndex * dayWidth + 2, moreY, Math.Max(0, dayWidth - 4), CalendarItemOverflow.MoreButtonHeight)));
            }
        }

        return (result, overflows);
    }

    /// <summary>
    /// The month grid: <see cref="MonthColumns"/> cells per week row, as many rows as <paramref name="dates"/> fills
    /// (<paramref name="height"/> is the height of <see cref="MonthRows"/> rows). Each cell stacks its items under the day
    /// label and reports the ones that do not fit as overflow (Windows MonthCalendarLayoutCalculator.CalculateRow).
    /// </summary>
    public static (List<CalendarLayoutMonthCell> Cells, List<CalendarLayoutPlacement> Items, List<CalendarLayoutOverflowCell> Overflows) CalculateMonth(
        IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(items);

        double cellWidth = width <= 0 ? 0 : width / MonthColumns;
        double cellHeight = height <= 0 ? 0 : height / MonthRows;
        var cells = new List<CalendarLayoutMonthCell>(dates.Count);
        for (int index = 0; index < dates.Count; index++)
        {
            int row = index / MonthColumns, column = index % MonthColumns;
            cells.Add(new CalendarLayoutMonthCell(dates[index], index, row, column, new CalendarLayoutRect(column * cellWidth, row * cellHeight, cellWidth, cellHeight)));
        }

        var placements = new List<CalendarLayoutPlacement>();
        var overflows = new List<CalendarLayoutOverflowCell>();
        var list = items as IList<ICalendarItem> ?? items.ToList();
        foreach (var cell in cells)
        {
            var cellItems = GetCellItems(list, cell.Date).ToList();
            if (cellItems.Count == 0) continue;
            double stackTop = cell.Bounds.Y + MonthDayLabelHeight + MonthCellPadding;
            double available = cell.Bounds.Bottom - MonthCellPadding - stackTop;
            var heights = new double[cellItems.Count];
            Array.Fill(heights, MonthItemHeight);
            int shown = CalendarItemOverflow.FitStack(heights, available, MonthItemGap, CalendarItemOverflow.MoreButtonHeight);
            double nextY = stackTop;
            double itemX = cell.Bounds.X + MonthCellPadding;
            double itemWidth = Math.Max(0, cell.Bounds.Width - MonthCellPadding * 2);
            for (int index = 0; index < shown; index++)
            {
                placements.Add(new CalendarLayoutPlacement(cellItems[index], cell.Date,
                    new CalendarLayoutRect(itemX, nextY, itemWidth, MonthItemHeight), CalendarPlacementKind.MonthCell));
                nextY += MonthItemHeight + MonthItemGap;
            }

            if (shown < cellItems.Count)
            {
                overflows.Add(new CalendarLayoutOverflowCell(cell.Date, cellItems.GetRange(shown, cellItems.Count - shown),
                    new CalendarLayoutRect(itemX, Math.Max(cell.Bounds.Y, cell.Bounds.Bottom - MonthCellPadding - CalendarItemOverflow.MoreButtonHeight),
                        itemWidth, CalendarItemOverflow.MoreButtonHeight)));
            }
        }

        return (cells, placements, overflows);
    }

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static DateOnly[] MonthGridDates(DateOnly rangeStart, DayOfWeek firstDayOfWeek)
    {
        int offset = ((int)rangeStart.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = rangeStart.AddDays(-offset);
        var dates = new DateOnly[MonthColumns * MonthRows];
        for (int index = 0; index < dates.Length; index++) dates[index] = start.AddDays(index);
        return dates;
    }

    private static CalendarPlacementKind TimedKind(ICalendarItem item) => item.IsMultiDayEvent ? CalendarPlacementKind.MultiDayGhost : CalendarPlacementKind.Timed;

    private static IEnumerable<ICalendarItem> GetCellItems(IList<ICalendarItem> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        foreach (var item in items)
        {
            var start = item.StartDate;
            var end = item.EndDate;
            if (end <= start) continue;
            if (start < dayEnd && end > dayStart) yield return item;
        }
    }

    private static List<ICalendarItem> BuildAllDayItems(IList<ICalendarItem> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var result = new List<ICalendarItem>();
        foreach (var item in items)
        {
            if (!item.IsAllDayEvent) continue;
            var start = item.StartDate;
            var end = item.EndDate;
            if (end <= start) continue;
            if (start < dayEnd && end > dayStart) result.Add(item);
        }

        return result;
    }

    private static List<Segment> BuildDaySegments(IList<ICalendarItem> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var segments = new List<Segment>();
        foreach (var item in items)
        {
            if (item.IsAllDayEvent) continue;
            var start = item.StartDate;
            var end = item.EndDate;
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
                if (columnEnds[column] <= segment.StartMinute)
                {
                    assigned = column;
                    columnEnds[column] = segment.EndMinute;
                    break;
                }
            }

            if (assigned < 0)
            {
                assigned = columnEnds.Count;
                columnEnds.Add(segment.EndMinute);
            }

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
