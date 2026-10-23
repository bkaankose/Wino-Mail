#nullable enable
using System;
using System.Collections.Generic;

namespace MailCal.Core.Domain.Models.Calendar;

/// <summary>
/// The date window the calendar keeps loaded around the visible period: the period itself plus
/// <see cref="PeriodsEachSide"/> periods before and after it. Items outside the window are
/// released when the window moves, so memory stays flat however far the user rails.
/// </summary>
public static class CalendarLoadWindow
{
    public const int PeriodsEachSide = 2;

    /// <summary>
    /// The exclusive date window to keep loaded for a visible range.
    /// </summary>
    public static DateRange GetWindow(VisibleDateRange visibleRange, CalendarSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(visibleRange);
        ArgumentNullException.ThrowIfNull(settings);

        var first = visibleRange;
        var last = visibleRange;

        for (var step = 0; step < PeriodsEachSide; step++)
        {
            first = CalendarRangeResolver.Navigate(first, -1, settings, today);
            last = CalendarRangeResolver.Navigate(last, 1, settings, today);
        }

        return new DateRange(
            first.StartDate.ToDateTime(TimeOnly.MinValue),
            last.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>
    /// The parts of <paramref name="target"/> that <paramref name="current"/> does not already
    /// cover: nothing, one range on either side, or the whole target when the two do not overlap.
    /// </summary>
    public static IReadOnlyList<DateRange> GetMissingRanges(DateRange? current, DateRange target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (current is null || current.EndDate <= target.StartDate || current.StartDate >= target.EndDate)
        {
            return new[] { target };
        }

        var missing = new List<DateRange>(2);

        if (target.StartDate < current.StartDate)
        {
            missing.Add(new DateRange(target.StartDate, current.StartDate));
        }

        if (target.EndDate > current.EndDate)
        {
            missing.Add(new DateRange(current.EndDate, target.EndDate));
        }

        return missing;
    }

    /// <summary>
    /// Whether an item that runs from <paramref name="start"/> to <paramref name="end"/> (exclusive)
    /// still touches the window and must stay loaded.
    /// </summary>
    public static bool Intersects(DateRange window, DateTime start, DateTime end)
    {
        ArgumentNullException.ThrowIfNull(window);

        return start < window.EndDate && end > window.StartDate;
    }

    public static bool IsSame(DateRange? left, DateRange? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        return left.StartDate == right.StartDate && left.EndDate == right.EndDate;
    }
}
