using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.WinUI.Controls.Calendar;

/// <summary>
/// The placement of one calendar item inside a single day column. Bounds are relative to the column.
/// </summary>
internal readonly record struct TimedItemLayout(CalendarItemViewModel Item, DateOnly Date, LayoutRect Bounds);

internal readonly record struct AllDayColumnLayout(List<TimedItemLayout> Items, int LaneCount, CalendarOverflowLayout? Overflow);

/// <summary>
/// An item drawn as a bar in the all-day strip: its days in the realized window and its lane.
/// </summary>
internal readonly record struct AllDaySpan(CalendarDaySpan Days, int Lane);

internal static class TimedCalendarLayoutCalculator
{
    internal const double ItemRightSpacing = CalendarEventPlacementCalculator.RightSpacing;

    private const double AllDayItemHeight = 32d;
    private const double AllDayItemGap = 4d;
    private const double AllDaySectionPadding = 6d;

    // Short events are laid out as if they lasted this tall, so a 15-minute event keeps its bar and a
    // compact title. Overlap columns are computed from the stretched end, so a following event moves
    // into the next column instead of being painted over.
    private const double MinimumItemHeight = 18d;

    public static double GetTimelineHeight(double hourHeight) => hourHeight * 24d;

    public static double GetAllDayHeight(int laneCount, bool hasMoreRow)
    {
        if (laneCount <= 0)
        {
            return 0d;
        }

        var height = (AllDaySectionPadding * 2d) +
                     (laneCount * AllDayItemHeight) +
                     ((laneCount - 1) * AllDayItemGap);

        return hasMoreRow
            ? height + CalendarItemOverflow.MoreButtonGap + CalendarItemOverflow.MoreButtonHeight
            : height;
    }

    public static LayoutRect GetAllDayMoreButtonBounds(double dayWidth)
        => new(
            2d,
            AllDaySectionPadding +
            (CalendarItemOverflow.MaxAllDayLanes * AllDayItemHeight) +
            ((CalendarItemOverflow.MaxAllDayLanes - 1) * AllDayItemGap) +
            CalendarItemOverflow.MoreButtonGap,
            Math.Max(0d, dayWidth - 4d),
            CalendarItemOverflow.MoreButtonHeight);

    /// <summary>
    /// Lays out the timed (not all-day) items of one date inside a column of <paramref name="dayWidth"/>.
    /// </summary>
    public static List<TimedItemLayout> CalculateDay(DateOnly date, IEnumerable<CalendarItemViewModel> items, double dayWidth, double hourHeight, CalendarEventDisplayMode displayMode = CalendarEventDisplayMode.Stacked)
    {
        var layouts = new List<TimedItemLayout>();
        var daySegments = BuildDaySegments(items, date, hourHeight)
            .OrderBy(segment => segment.StartMinute)
            .ThenBy(segment => segment.EndMinute)
            .ToList();

        if (displayMode is CalendarEventDisplayMode.Overlapped or CalendarEventDisplayMode.LimitedOverlap or CalendarEventDisplayMode.ProtectTitles)
        {
            var placements = CalendarEventPlacementCalculator.Calculate(daySegments.Select((segment, index) =>
                new CalendarOverlapInterval(index, segment.Item.Id, segment.StartMinute, segment.EndMinute)), dayWidth, hourHeight, displayMode);

            foreach (var placement in placements)
            {
                var segment = daySegments[placement.SourceIndex];
                var y = (segment.StartMinute / 60d) * hourHeight;
                var height = Math.Max(1d, ((segment.EndMinute - segment.StartMinute) / 60d) * hourHeight);

                layouts.Add(new TimedItemLayout(segment.Item, date,
                    new LayoutRect(placement.Left, y, placement.Width + ItemRightSpacing, height)));
            }

            return layouts;
        }

        foreach (var cluster in BuildClusters(daySegments))
        {
            AssignColumns(cluster);
            var columnCount = cluster.Max(segment => segment.ColumnIndex) + 1;
            var subColumnWidth = columnCount == 0 ? dayWidth : dayWidth / columnCount;

            foreach (var segment in cluster)
            {
                var x = (segment.ColumnIndex * subColumnWidth) + 2;
                var width = Math.Max(0, subColumnWidth - 4);
                var y = (segment.StartMinute / 60d) * hourHeight;
                var height = Math.Max(1, ((segment.EndMinute - segment.StartMinute) / 60d) * hourHeight);

                layouts.Add(new TimedItemLayout(segment.Item, date, new LayoutRect(x, y, width, height)));
            }
        }

        return layouts;
    }

    /// <summary>
    /// Packs the bars of the all-day strip (all-day events and timed events of 24 hours or more) over
    /// the realized window, so one bar keeps its lane on every day it covers.
    /// </summary>
    public static Dictionary<CalendarItemViewModel, AllDaySpan> AssignAllDaySpans(IReadOnlyList<DateOnly> windowDates, IEnumerable<CalendarItemViewModel> items)
    {
        var spanItems = new List<CalendarItemViewModel>();
        var spans = new List<CalendarDaySpan>();

        foreach (var item in items
                     .Where(item => item.ShowsInAllDayArea)
                     .OrderBy(item => item.StartDate)
                     .ThenBy(item => item.Title, StringComparer.CurrentCulture))
        {
            if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end) ||
                CalendarSpanLanes.GetSpan(windowDates, start.LocalDateTime, end.LocalDateTime) is not CalendarDaySpan span)
            {
                continue;
            }

            spanItems.Add(item);
            spans.Add(span);
        }

        var lanes = CalendarSpanLanes.Assign(spans);
        var result = new Dictionary<CalendarItemViewModel, AllDaySpan>(spanItems.Count);

        for (var index = 0; index < spanItems.Count; index++)
        {
            result[spanItems[index]] = new AllDaySpan(spans[index], lanes[index]);
        }

        return result;
    }

    /// <summary>
    /// Lays out the all-day strip of the day at <paramref name="windowIndex"/>. A bar is placed by the
    /// day it starts on and spans the following days; one that runs across <paramref name="breakIndex"/>
    /// (the first day of the shown period) restarts there, so its title stays in view. Bars on lanes
    /// past <see cref="CalendarItemOverflow.MaxAllDayLanes"/> go to the day's overflow link.
    /// </summary>
    public static AllDayColumnLayout CalculateAllDayColumn(int windowIndex, DateOnly date, IReadOnlyDictionary<CalendarItemViewModel, AllDaySpan> spans, int breakIndex, double dayWidth)
    {
        var layouts = new List<TimedItemLayout>();
        var hiddenItems = new List<CalendarItemViewModel>();
        var laneCount = 0;

        foreach (var (item, span) in spans)
        {
            if (!span.Days.Covers(windowIndex))
            {
                continue;
            }

            if (span.Lane >= CalendarItemOverflow.MaxAllDayLanes)
            {
                hiddenItems.Add(item);
                continue;
            }

            laneCount = Math.Max(laneCount, span.Lane + 1);

            var isBroken = span.Days.FirstIndex < breakIndex && breakIndex <= span.Days.LastIndex;
            var segmentStart = isBroken && windowIndex >= breakIndex ? breakIndex : span.Days.FirstIndex;
            var segmentEnd = isBroken && windowIndex < breakIndex ? breakIndex - 1 : span.Days.LastIndex;

            if (windowIndex != segmentStart)
            {
                continue;
            }

            var y = AllDaySectionPadding + (span.Lane * (AllDayItemHeight + AllDayItemGap));
            var width = Math.Max(0d, ((segmentEnd - segmentStart + 1) * dayWidth) - 4d);

            layouts.Add(new TimedItemLayout(item, date, new LayoutRect(2d, y, width, AllDayItemHeight)));
        }

        hiddenItems.Sort(static (left, right) => left.StartDate.CompareTo(right.StartDate));

        CalendarOverflowLayout? overflow = hiddenItems.Count > 0
            ? new CalendarOverflowLayout(date, 0, hiddenItems, GetAllDayMoreButtonBounds(dayWidth))
            : null;

        return new AllDayColumnLayout(layouts, laneCount, overflow);
    }

    private static List<Segment> BuildDaySegments(IEnumerable<CalendarItemViewModel> items, DateOnly date, double hourHeight)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var segments = new List<Segment>();
        var minimumMinutes = hourHeight > 0d ? MinimumItemHeight * 60d / hourHeight : 0d;
        var dayMinutes = (dayEnd - dayStart).TotalMinutes;

        foreach (var item in items)
        {
            if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end))
            {
                continue;
            }

            if (item.ShowsInAllDayArea)
            {
                continue;
            }

            var localStart = start.LocalDateTime;
            var localEnd = end.LocalDateTime;

            if (localEnd <= localStart)
            {
                continue;
            }

            var segmentStart = localStart > dayStart ? localStart : dayStart;
            var segmentEnd = localEnd < dayEnd ? localEnd : dayEnd;

            if (segmentEnd <= segmentStart)
            {
                continue;
            }

            var startMinute = (segmentStart - dayStart).TotalMinutes;
            var endMinute = Math.Max((segmentEnd - dayStart).TotalMinutes, Math.Min(dayMinutes, startMinute + minimumMinutes));

            segments.Add(new Segment(item, startMinute, endMinute));
        }

        return segments;
    }

    private static IEnumerable<List<Segment>> BuildClusters(List<Segment> segments)
    {
        if (segments.Count == 0)
        {
            yield break;
        }

        var cluster = new List<Segment> { segments[0] };
        var clusterEnd = segments[0].EndMinute;

        for (var index = 1; index < segments.Count; index++)
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
            var assignedColumn = -1;

            for (var columnIndex = 0; columnIndex < columnEnds.Count; columnIndex++)
            {
                if (columnEnds[columnIndex] <= segment.StartMinute)
                {
                    assignedColumn = columnIndex;
                    columnEnds[columnIndex] = segment.EndMinute;
                    break;
                }
            }

            if (assignedColumn < 0)
            {
                assignedColumn = columnEnds.Count;
                columnEnds.Add(segment.EndMinute);
            }

            segment.ColumnIndex = assignedColumn;
        }
    }

    private sealed record Segment(CalendarItemViewModel Item, double StartMinute, double EndMinute)
    {
        public int ColumnIndex { get; set; }
    }
}
