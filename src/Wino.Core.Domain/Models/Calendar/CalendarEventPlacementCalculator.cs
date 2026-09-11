using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>Horizontal card geometry in paint order, shared by the calendar and settings preview.</summary>
public static class CalendarEventPlacementCalculator
{
    public const double RightSpacing = 10d;
    public const double ProtectedTitleHeight = 32d;

    public static IReadOnlyList<CalendarOverlapPlacement> Calculate(
        IEnumerable<CalendarOverlapInterval> intervals, double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
    {
        dayWidth = double.IsFinite(dayWidth) ? Math.Max(0, dayWidth) : 0;
        hourHeight = double.IsFinite(hourHeight) && hourHeight > 0 ? hourHeight : 60;
        var usableWidth = Math.Max(0, dayWidth - 4 - RightSpacing);
        var ordered = intervals.Where(item => double.IsFinite(item.StartMinute) && double.IsFinite(item.EndMinute) && item.EndMinute > item.StartMinute)
            .OrderBy(item => item.StartMinute)
            .ThenBy(item => mode == CalendarEventDisplayMode.Stacked ? item.EndMinute : -item.EndMinute)
            .ThenBy(item => item.Id)
            .ThenBy(item => item.SourceIndex)
            .ToList();

        if (mode == CalendarEventDisplayMode.Overlapped)
        {
            return OverlappedCalendarLayout.Calculate(ordered, usableWidth)
                .Select(item => item with { Left = item.Left + Math.Min(2, dayWidth) }).ToArray();
        }

        var result = new List<CalendarOverlapPlacement>();
        var group = new List<(CalendarOverlapInterval Item, int Lane)>();
        var laneEnds = new List<double>();
        var groupEnd = double.NegativeInfinity;

        foreach (var item in ordered)
        {
            if (item.StartMinute >= groupEnd)
            {
                Flush();
            }

            var end = mode == CalendarEventDisplayMode.ProtectTitles
                ? Math.Min(item.EndMinute, item.StartMinute + ProtectedTitleHeight * 60 / hourHeight)
                : item.EndMinute;
            var lane = laneEnds.FindIndex(value => value <= item.StartMinute);

            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(end);
            }
            else
            {
                laneEnds[lane] = end;
            }

            group.Add((item, lane));
            groupEnd = Math.Max(groupEnd, end);
        }

        Flush();
        return result;

        void Flush()
        {
            if (group.Count == 0)
                return;

            // Right-hand lanes paint last in Limited overlap, including lanes reused later.
            IEnumerable<(CalendarOverlapInterval Item, int Lane)> paintOrder = mode == CalendarEventDisplayMode.LimitedOverlap
                ? group.OrderBy(entry => entry.Lane).ThenBy(entry => entry.Item.StartMinute)
                : group;
            var columnWidth = (mode == CalendarEventDisplayMode.Stacked ? dayWidth : usableWidth) / laneEnds.Count;

            foreach (var entry in paintOrder)
            {
                var offset = entry.Lane * columnWidth;
                var width = mode switch
                {
                    CalendarEventDisplayMode.LimitedOverlap => Math.Min(usableWidth - offset, columnWidth * 1.3),
                    CalendarEventDisplayMode.ProtectTitles => Math.Max(0, columnWidth - 2),
                    _ => Math.Max(0, columnWidth - 4 - RightSpacing)
                };
                result.Add(new(entry.Item.SourceIndex, Math.Min(dayWidth, 2 + offset), width));
            }

            group.Clear();
            laneEnds.Clear();
            groupEnd = double.NegativeInfinity;
        }
    }
}
