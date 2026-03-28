using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public sealed class TimedItemLayout
{
    public TimedItemLayout(CalendarItemViewModel item, int dayIndex, DateOnly date, LayoutRect bounds, DataTemplate? template = null)
    {
        Item = item;
        DayIndex = dayIndex;
        Date = date;
        Bounds = bounds;
        Template = template;
    }

    public CalendarItemViewModel Item { get; set; }
    public int DayIndex { get; set; }
    public DateOnly Date { get; set; }
    public LayoutRect Bounds { get; set; }
    public DataTemplate? Template { get; set; }
}

internal sealed record TimedCalendarLayoutResult(IReadOnlyList<DateOnly> VisibleDates, double DayWidth, IReadOnlyList<TimedItemLayout> Items);

internal static class TimedCalendarLayoutCalculator
{
    private const double AllDayItemHeight = 28d;
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

    public static TimedCalendarLayoutResult Calculate(VisibleDateRange range, IEnumerable<CalendarItemViewModel> items, double availableWidth, double hourHeight)
    {
        var visibleDates = range.Dates;
        var dayWidth = visibleDates.Count == 0 ? 0d : availableWidth / visibleDates.Count;
        var layouts = new List<TimedItemLayout>();

        for (var dayIndex = 0; dayIndex < visibleDates.Count; dayIndex++)
        {
            var date = visibleDates[dayIndex];
            var daySegments = BuildDaySegments(items, date)
                .OrderBy(segment => segment.StartMinute)
                .ThenBy(segment => segment.EndMinute)
                .ToList();

            foreach (var cluster in BuildClusters(daySegments))
            {
                AssignColumns(cluster);
                var columnCount = cluster.Max(segment => segment.ColumnIndex) + 1;
                var subColumnWidth = columnCount == 0 ? dayWidth : dayWidth / columnCount;

                foreach (var segment in cluster)
                {
                    var x = (dayIndex * dayWidth) + (segment.ColumnIndex * subColumnWidth) + 2;
                    var width = Math.Max(0, subColumnWidth - 4);
                    var y = (segment.StartMinute / 60d) * hourHeight;
                    var height = Math.Max(1, ((segment.EndMinute - segment.StartMinute) / 60d) * hourHeight);

                    layouts.Add(new TimedItemLayout(segment.Item, dayIndex, date, new LayoutRect(x, y, width, height)));
                }
            }
        }

        return new TimedCalendarLayoutResult(visibleDates, dayWidth, layouts);
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

    public static IReadOnlyList<TimedItemLayout> CalculateAllDayItems(VisibleDateRange range, IEnumerable<CalendarItemViewModel> items, double availableWidth)
    {
        var visibleDates = range.Dates;
        var dayWidth = visibleDates.Count == 0 ? 0d : availableWidth / visibleDates.Count;
        var layouts = new List<TimedItemLayout>();

        for (var dayIndex = 0; dayIndex < visibleDates.Count; dayIndex++)
        {
            var date = visibleDates[dayIndex];
            var dayItems = BuildAllDayItems(items, date)
                .OrderBy(item => item.StartDate)
                .ThenBy(item => item.EndDate)
                .ThenBy(item => item.Title)
                .ToList();

            for (var rowIndex = 0; rowIndex < dayItems.Count; rowIndex++)
            {
                var y = AllDaySectionPadding + (rowIndex * (AllDayItemHeight + AllDayItemGap));
                var x = (dayIndex * dayWidth) + 2d;
                var width = Math.Max(0d, dayWidth - 4d);

                layouts.Add(new TimedItemLayout(
                    dayItems[rowIndex],
                    dayIndex,
                    date,
                    new LayoutRect(x, y, width, AllDayItemHeight)));
            }
        }

        return layouts;
    }

    public static int GetAllDayLaneCount(IReadOnlyList<DateOnly> visibleDates, IEnumerable<CalendarItemViewModel> items)
    {
        var laneCount = 0;

        foreach (var date in visibleDates)
        {
            laneCount = Math.Max(laneCount, BuildAllDayItems(items, date).Count);
        }

        return laneCount;
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
