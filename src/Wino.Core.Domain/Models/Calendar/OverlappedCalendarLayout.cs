using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Calendar;

public readonly record struct CalendarOverlapInterval(int SourceIndex, Guid Id, double StartMinute, double EndMinute);

public readonly record struct CalendarOverlapPlacement(int SourceIndex, double Left, double Width);

/// <summary>Calculates horizontal bounds in drawing order for one day's clipped timed events.</summary>
public static class OverlappedCalendarLayout
{
    public static IReadOnlyList<CalendarOverlapPlacement> Calculate(IEnumerable<CalendarOverlapInterval> intervals, double usableWidth)
    {
        usableWidth = double.IsFinite(usableWidth) ? Math.Max(0, usableWidth) : 0;

        var ordered = intervals
            .Where(item => double.IsFinite(item.StartMinute) && double.IsFinite(item.EndMinute) && item.EndMinute > item.StartMinute)
            .OrderBy(item => item.StartMinute)
            .ThenByDescending(item => item.EndMinute)
            .ThenBy(item => item.Id)
            .ThenBy(item => item.SourceIndex);
        var placements = new List<CalendarOverlapPlacement>();
        var active = new List<(double End, int Depth)>();
        var cluster = new List<(int SourceIndex, int Depth)>();

        foreach (var interval in ordered)
        {
            active.RemoveAll(item => item.End <= interval.StartMinute);

            if (active.Count == 0)
            {
                FlushCluster();
            }

            var depth = active.Count == 0 ? 0 : active.Max(item => item.Depth) + 1;
            active.Add((interval.EndMinute, depth));
            cluster.Add((interval.SourceIndex, depth));
        }

        FlushCluster();
        return placements;

        void FlushCluster()
        {
            if (cluster.Count == 0)
                return;

            var maxDepth = cluster.Max(item => item.Depth);
            var indent = maxDepth == 0 ? 0 : Math.Min(24d, usableWidth * 0.4d / maxDepth);

            foreach (var item in cluster)
            {
                var left = item.Depth * indent;
                placements.Add(new CalendarOverlapPlacement(item.SourceIndex, left, usableWidth - left));
            }

            cluster.Clear();
        }
    }
}
