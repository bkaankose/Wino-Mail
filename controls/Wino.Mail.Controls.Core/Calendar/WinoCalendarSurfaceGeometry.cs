using Wino.Mail.Controls.Core.Layout;

namespace Wino.Mail.Controls.Core.Calendar;

/// <summary>Where a dragged calendar tile lands: the snapped times and the ghost frame in canvas coordinates.</summary>
public readonly record struct WinoCalendarDragTarget(DateTime Start, DateTime End, WinoLayoutFrame Frame);

/// <summary>
/// Hit testing, snapping and drag math of the native calendar surface (Windows CalendarPeriodControl): the hour
/// gutter, the scrolling timeline with its 30-minute grid, the all-day strip and the month grid. Coordinates are
/// top-down (flipped) and relative to the canvas that hosts the tiles; the timed canvas starts
/// <see cref="TimelinePadding"/> above midnight and its day columns start after <see cref="HourColumnWidth"/>.
/// </summary>
public static class WinoCalendarSurfaceGeometry
{
    public const double HourColumnWidth = 64;
    public const double TimedHeaderHeight = 44;
    public const double MonthHeaderHeight = 36;
    public const double TimelinePadding = 8;
    public const double GridIntervalMinutes = 30;
    public const double DefaultHourHeight = 52;

    /// <summary>Clickable slots per day in the timed grid.</summary>
    public const int SlotsPerDay = (int)(24 * 60 / GridIntervalMinutes);

    /// <summary>A press that moves less than this is a click, not a range selection.</summary>
    public const double SelectionDragThreshold = 3;

    /// <summary>A tile press that moves less than this is a click, not a move.</summary>
    public const double TileDragThreshold = 4;

    /// <summary>Height of the bottom-edge strip that resizes a timed tile instead of moving it.</summary>
    public const double ResizeHandleHeight = 6;

    /// <summary>The configured hour height, or <see cref="DefaultHourHeight"/>.</summary>
    public static double HourHeight(double configured) => configured > 0 ? configured : DefaultHourHeight;

    /// <summary>Height of the timed canvas: 24 hours plus the padding above and below.</summary>
    public static double CanvasHeight(double hourHeight) => hourHeight * 24 + TimelinePadding * 2;

    /// <summary>Width of one day column in a timed canvas <paramref name="contentWidth"/> wide (gutter excluded).</summary>
    public static double DayWidth(double contentWidth, int dayCount)
        => dayCount <= 0 ? 0 : Math.Max(0, contentWidth - HourColumnWidth) / dayCount;

    /// <summary>The x of a day column's leading edge.</summary>
    public static double DayX(int dayIndex, double dayWidth) => HourColumnWidth + dayIndex * dayWidth;

    /// <summary>The y of a time of day on the timed canvas.</summary>
    public static double TimeY(TimeSpan timeOfDay, double hourHeight) => TimelinePadding + timeOfDay.TotalHours * hourHeight;

    /// <summary>The first scroll offset: half an hour before <paramref name="startHour"/>, kept inside the content.</summary>
    public static double InitialScrollOffset(double startHour, double hourHeight, double contentHeight, double viewportHeight)
        => Math.Max(0, Math.Min((startHour - 0.5) * hourHeight, contentHeight - viewportHeight));

    /// <summary>Weekend columns are shaded only when more than one day is shown.</summary>
    public static bool IsShadedWeekend(DateOnly date, int dayCount)
        => dayCount > 1 && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>The day column under <paramref name="x"/>, clamped to the visible days; -1 without days.</summary>
    public static int DayIndexAt(double x, int dayCount, double dayWidth)
    {
        if (dayCount <= 0 || dayWidth <= 0) return -1;

        return Math.Clamp((int)Math.Floor((x - HourColumnWidth) / dayWidth), 0, dayCount - 1);
    }

    /// <summary>The 30-minute slot under <paramref name="y"/>, clamped to the day.</summary>
    public static int SlotAt(double y, double hourHeight)
    {
        double intervalHeight = hourHeight * GridIntervalMinutes / 60;
        if (intervalHeight <= 0) return 0;

        return Math.Clamp((int)((y - TimelinePadding) / intervalHeight), 0, SlotsPerDay - 1);
    }

    /// <summary>The rectangle of a 30-minute slot, the anchor of a slot click.</summary>
    public static WinoLayoutFrame SlotBounds(int dayIndex, int slot, double dayWidth, double hourHeight)
    {
        double intervalHeight = hourHeight * GridIntervalMinutes / 60;
        return new WinoLayoutFrame(DayX(dayIndex, dayWidth), TimelinePadding + slot * intervalHeight, dayWidth, intervalHeight);
    }

    /// <summary>The start of the 30-minute slot a click at (x, y) picks; null outside the day columns.</summary>
    public static DateTime? SlotStartAt(double x, double y, IReadOnlyList<DateOnly> dates, double dayWidth, double hourHeight)
    {
        ArgumentNullException.ThrowIfNull(dates);
        if (dayWidth <= 0 || dates.Count == 0 || x < HourColumnWidth) return null;

        int dayIndex = Math.Clamp((int)((x - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
        int slot = SlotAt(y, hourHeight);
        return dates[dayIndex].ToDateTime(TimeOnly.MinValue).AddMinutes(slot * GridIntervalMinutes);
    }

    /// <summary>Rounds to the nearest multiple of <paramref name="snapMinutes"/> (at least one minute).</summary>
    public static DateTime Snap(DateTime value, int snapMinutes)
    {
        long interval = TimeSpan.FromMinutes(Math.Max(1, snapMinutes)).Ticks;
        return new DateTime((value.Ticks + interval / 2) / interval * interval, value.Kind);
    }

    /// <summary>The snap cell under a point in the timed grid, clamped to the visible days and the day's last cell.</summary>
    public static DateTime? TimedCellAt(double x, double y, IReadOnlyList<DateOnly> dates, double dayWidth, double hourHeight, int snapMinutes)
    {
        ArgumentNullException.ThrowIfNull(dates);
        if (dates.Count == 0 || dayWidth <= 0 || hourHeight <= 0) return null;

        int dayIndex = DayIndexAt(x, dates.Count, dayWidth);
        int snap = Math.Max(1, snapMinutes);
        double minutes = (y - TimelinePadding) / hourHeight * 60;
        int cell = Math.Clamp((int)Math.Floor(minutes / snap), 0, 24 * 60 / snap - 1);
        return dates[dayIndex].ToDateTime(TimeOnly.MinValue).AddMinutes(cell * snap);
    }

    /// <summary>The highlight of a selected range's part on one day column, inset from the column edges.</summary>
    public static WinoLayoutFrame SelectionSegmentBounds(int dayIndex, DateTime segmentStart, DateTime segmentEnd, double dayWidth, double hourHeight)
    {
        double top = TimeY(segmentStart.TimeOfDay, hourHeight);
        double height = (segmentEnd - segmentStart).TotalHours * hourHeight;
        return new WinoLayoutFrame(DayX(dayIndex, dayWidth) + 1, top, Math.Max(0, dayWidth - 3), Math.Max(2, height));
    }

    /// <summary>All-day tiles move by whole days along the strip.</summary>
    public static WinoCalendarDragTarget? ResolveAllDayDrag(int dayCount, double dayWidth, double downX, double pointX,
        DateTime originalStart, DateTime originalEnd, WinoLayoutFrame originFrame)
    {
        if (dayWidth <= 0 || dayCount <= 0) return null;

        int delta = DayIndexAt(pointX, dayCount, dayWidth) - DayIndexAt(downX, dayCount, dayWidth);
        return new WinoCalendarDragTarget(originalStart.AddDays(delta), originalEnd.AddDays(delta),
            originFrame with { X = originFrame.X + delta * dayWidth });
    }

    /// <summary>
    /// Timed tiles move by day and by the snap interval, keeping their duration and staying inside the visible days;
    /// a resize keeps the start and moves the end within the start's day (at least one snap interval long).
    /// <paramref name="tileDate"/> is the day the dragged tile is drawn on.
    /// </summary>
    public static WinoCalendarDragTarget? ResolveTimedDrag(IReadOnlyList<DateOnly> dates, double dayWidth, double hourHeight, int snapMinutes,
        bool resize, (double X, double Y) down, (double X, double Y) point,
        DateTime originalStart, DateTime originalEnd, WinoLayoutFrame originFrame, DateOnly tileDate)
    {
        ArgumentNullException.ThrowIfNull(dates);
        if (dayWidth <= 0 || dates.Count == 0 || hourHeight <= 0) return null;

        double deltaMinutes = (point.Y - down.Y) / hourHeight * 60;
        if (resize)
        {
            var minimum = originalStart.AddMinutes(snapMinutes);
            var maximum = originalStart.Date.AddDays(1);
            var end = Snap(originalEnd.AddMinutes(deltaMinutes), snapMinutes);
            end = end < minimum ? minimum : end > maximum ? maximum : end;
            double height = originFrame.Height + (end - originalEnd).TotalHours * hourHeight;
            return new WinoCalendarDragTarget(originalStart, end, originFrame with { Height = Math.Max(4, height) });
        }

        int deltaDays = DayIndexAt(point.X, dates.Count, dayWidth) - DayIndexAt(down.X, dates.Count, dayWidth);
        var duration = originalEnd - originalStart;
        var start = Snap(originalStart.AddDays(deltaDays).AddMinutes(deltaMinutes), snapMinutes);
        var firstDay = dates[0].ToDateTime(TimeOnly.MinValue);
        var lastStart = dates[^1].ToDateTime(TimeOnly.MinValue).AddDays(1).AddMinutes(-snapMinutes);
        start = start < firstDay ? firstDay : start > lastStart ? lastStart : start;

        int originIndex = IndexOf(dates, tileDate);
        int targetIndex = IndexOf(dates, DateOnly.FromDateTime(start));
        double columnOffset = originFrame.X - DayX(Math.Max(0, originIndex), dayWidth);
        double rowOffset = originFrame.Y - TimeY(originalStart.TimeOfDay, hourHeight);
        double y = TimeY(start.TimeOfDay, hourHeight) + rowOffset;
        double bottom = TimelinePadding + 24 * hourHeight;
        double ghostHeight = Math.Max(4, Math.Min(originFrame.Height, bottom - y));
        return new WinoCalendarDragTarget(start, start + duration,
            new WinoLayoutFrame(DayX(Math.Max(0, targetIndex), dayWidth) + columnOffset, y, originFrame.Width, ghostHeight));
    }

    /// <summary>Month tiles move by whole days to the target cell, keeping their time of day.</summary>
    public static WinoCalendarDragTarget ResolveMonthDrag(DateOnly originDate, WinoLayoutFrame originCell, DateOnly targetDate, WinoLayoutFrame targetCell,
        DateTime originalStart, DateTime originalEnd, WinoLayoutFrame originFrame)
    {
        int delta = targetDate.DayNumber - originDate.DayNumber;
        return new WinoCalendarDragTarget(originalStart.AddDays(delta), originalEnd.AddDays(delta),
            originFrame with { X = originFrame.X + targetCell.X - originCell.X, Y = originFrame.Y + targetCell.Y - originCell.Y });
    }

    /// <summary>The pointer offset a scripted month drag applies for a move of <paramref name="days"/> days.</summary>
    public static (double Width, double Height) MonthPreviewOffset(int days, double cellWidth, double cellHeight)
    {
        int rows = (int)Math.Floor(days / 7.0);
        return ((days - rows * 7) * cellWidth, rows * cellHeight);
    }

    /// <summary>The index of <paramref name="date"/> in <paramref name="dates"/>, or -1.</summary>
    public static int IndexOf(IReadOnlyList<DateOnly> dates, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(dates);
        for (int index = 0; index < dates.Count; index++) if (dates[index] == date) return index;
        return -1;
    }
}
