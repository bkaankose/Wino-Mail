#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// One bar in the all-day area or a month week row: the first and last day column it covers.
/// </summary>
public readonly record struct CalendarDaySpan(int FirstIndex, int LastIndex)
{
    public int Length => LastIndex - FirstIndex + 1;

    public bool Covers(int index) => index >= FirstIndex && index <= LastIndex;
}

/// <summary>
/// Packs day spans into lanes, as Outlook and Google Calendar do: each span sits on the lowest lane that
/// is free on all its days, spans that start earlier go first, and of two that start on the same day the
/// longer one goes first. All-day events and timed events of 24 hours or more are drawn this way, as one
/// bar per lane instead of one chip per day.
/// </summary>
public static class CalendarSpanLanes
{
    /// <summary>Returns the lane of each span, in the order of <paramref name="spans"/>.</summary>
    public static int[] Assign(IReadOnlyList<CalendarDaySpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        var lanes = new int[spans.Count];
        var order = new int[spans.Count];

        for (var index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        Array.Sort(order, (left, right) =>
        {
            var byStart = spans[left].FirstIndex.CompareTo(spans[right].FirstIndex);
            if (byStart != 0)
                return byStart;

            var byLength = spans[right].Length.CompareTo(spans[left].Length);
            return byLength != 0 ? byLength : left.CompareTo(right);
        });

        var laneEnds = new List<int>();

        foreach (var spanIndex in order)
        {
            var span = spans[spanIndex];
            var lane = laneEnds.FindIndex(lastIndex => lastIndex < span.FirstIndex);

            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(span.LastIndex);
            }
            else
            {
                laneEnds[lane] = span.LastIndex;
            }

            lanes[spanIndex] = lane;
        }

        return lanes;
    }

    /// <summary>
    /// The first and last of <paramref name="dates"/> that an event from <paramref name="start"/> to
    /// <paramref name="end"/> (local, end exclusive) touches, or null when it touches none.
    /// </summary>
    public static CalendarDaySpan? GetSpan(IReadOnlyList<DateOnly> dates, DateTime start, DateTime end)
    {
        ArgumentNullException.ThrowIfNull(dates);

        if (end <= start)
            return null;

        var first = -1;
        var last = -1;

        for (var index = 0; index < dates.Count; index++)
        {
            var dayStart = dates[index].ToDateTime(TimeOnly.MinValue);

            if (start < dayStart.AddDays(1) && end > dayStart)
            {
                if (first < 0)
                    first = index;

                last = index;
            }
        }

        return first < 0 ? null : new CalendarDaySpan(first, last);
    }
}
