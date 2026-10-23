using System;
using System.Collections.Generic;
using System.Linq;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Calendar.Controls;

/// <summary>
/// The placement of one calendar item inside a single day column. Bounds are relative to the column.
/// </summary>
internal readonly record struct TimedItemLayout(CalendarItemViewModel Item, DateOnly Date, LayoutRect Bounds);

internal static class TimedCalendarLayoutCalculator
{
    internal const double ItemRightSpacing = CalendarEventPlacementCalculator.RightSpacing;

    private const double AllDayItemHeight = 32d;
    private const double AllDayItemGap = 4d;
    private const double AllDaySectionPadding = 6d;

    public static double GetTimelineHeight(double hourHeight) => hourHeight * 24d;

    public static double GetAllDayHeight(int laneCount)
    {
        if (laneCount <= 0)
        {
            return 0d;
        }

        return (AllDaySectionPadding * 2d) +
               (laneCount * AllDayItemHeight) +
               ((laneCount - 1) * AllDayItemGap);
    }

    /// <summary>
    /// Lays out the timed (not all-day) items of one date inside a column of <paramref name="dayWidth"/>.
    /// </summary>
    public static List<TimedItemLayout> CalculateDay(DateOnly date, IEnumerable<CalendarItemViewModel> items, double dayWidth, double hourHeight, CalendarEventDisplayMode displayMode = CalendarEventDisplayMode.Stacked)
    {
        var layouts = new List<TimedItemLayout>();
        var daySegments = BuildDaySegments(items, date)
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
    /// Stacks the all-day items of one date inside a column of <paramref name="dayWidth"/>. The
    /// number of returned items is the column's lane count.
    /// </summary>
    public static List<TimedItemLayout> CalculateAllDayItems(DateOnly date, IEnumerable<CalendarItemViewModel> items, double dayWidth)
    {
        var layouts = new List<TimedItemLayout>();
        var dayItems = BuildAllDayItems(items, date)
            .OrderBy(item => item.StartDate)
            .ThenBy(item => item.EndDate)
            .ThenBy(item => item.Title)
            .ToList();

        for (var rowIndex = 0; rowIndex < dayItems.Count; rowIndex++)
        {
            var y = AllDaySectionPadding + (rowIndex * (AllDayItemHeight + AllDayItemGap));
            var width = Math.Max(0d, dayWidth - 4d);

            layouts.Add(new TimedItemLayout(
                dayItems[rowIndex],
                date,
                new LayoutRect(2d, y, width, AllDayItemHeight)));
        }

        return layouts;
    }

    private static List<Segment> BuildDaySegments(IEnumerable<CalendarItemViewModel> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var segments = new List<Segment>();

        foreach (var item in items)
        {
            if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end))
            {
                continue;
            }

            if (item.IsAllDayEvent)
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

            segments.Add(new Segment(item, (segmentStart - dayStart).TotalMinutes, (segmentEnd - dayStart).TotalMinutes));
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

    private static List<CalendarItemViewModel> BuildAllDayItems(IEnumerable<CalendarItemViewModel> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var allDayItems = new List<CalendarItemViewModel>();

        foreach (var item in items)
        {
            if (!item.IsAllDayEvent)
            {
                continue;
            }

            if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end))
            {
                continue;
            }

            var localStart = start.LocalDateTime;
            var localEnd = end.LocalDateTime;

            if (localEnd <= localStart)
            {
                continue;
            }

            if (localStart < dayEnd && localEnd > dayStart)
            {
                allDayItems.Add(item);
            }
        }

        return allDayItems;
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
