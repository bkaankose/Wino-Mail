using System;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// A continuous selection with an inclusive start and exclusive end.
/// </summary>
public sealed record CalendarSelectionRange(DateTime Start, DateTime End)
{
    public static CalendarSelectionRange FromCells(DateTime anchor, DateTime current, TimeSpan cellDuration)
    {
        if (cellDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cellDuration));

        return new CalendarSelectionRange(
            anchor < current ? anchor : current,
            (anchor > current ? anchor : current).Add(cellDuration));
    }

    public CalendarSelectionRange IntersectDay(DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var start = Start > dayStart ? Start : dayStart;
        var end = End < dayEnd ? End : dayEnd;

        return start < end ? new CalendarSelectionRange(start, end) : null;
    }
}
