using CoreGraphics;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>
/// AppKit placement of the bars of the all-day strip and the month grid (the port of the Windows
/// TimedCalendarLayoutCalculator.AssignAllDaySpans and MonthCalendarLayoutCalculator.CalculateRow). An all-day or
/// multi-day item is one bar across the day columns it covers, on a lane that is free on all of them; the other
/// month events fill the free slots of each day under the bars.
/// </summary>
internal static class CalendarSpanLayout
{
    private const int Columns = CalendarSurfaceLayoutCalculator.MonthColumns;
    private const int MaxLanes = CalendarItemOverflow.MaxAllDayLanes;
    private const double AllDayColumnInset = 2;
    private const double AllDayBarInset = 4;
    private const double MonthItemPitch = CalendarSurfaceLayoutCalculator.MonthItemHeight + CalendarSurfaceLayoutCalculator.MonthItemGap;

    public static (int Lanes, bool HasOverflow) GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
    {
        var lanes = 0;
        var hasOverflow = false;

        foreach (var bar in AssignBars(dates, items))
        {
            if (bar.Lane < MaxLanes) lanes = Math.Max(lanes, bar.Lane + 1);
            else hasOverflow = true;
        }

        return (lanes, hasOverflow);
    }

    /// <summary>
    /// The bars of the all-day strip over the realized days. A bar that runs across <paramref name="breakIndex"/>
    /// (the first day of the shown period) is drawn again from there, so its title stays in view. Bars on lanes
    /// past <see cref="CalendarItemOverflow.MaxAllDayLanes"/> go to the "n more" link of each day they cover.
    /// </summary>
    public static (List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateAllDay(
        IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth, int breakIndex)
    {
        var placements = new List<CalendarItemPlacement>();
        var hiddenByDay = new List<List<ICalendarItem>>(dates.Count);
        for (var index = 0; index < dates.Count; index++) hiddenByDay.Add(new List<ICalendarItem>());

        foreach (var bar in AssignBars(dates, items))
        {
            if (bar.Lane >= MaxLanes)
            {
                for (var day = bar.Span.FirstIndex; day <= bar.Span.LastIndex; day++) hiddenByDay[day].Add(bar.Item);
                continue;
            }

            var y = CalendarSurfaceLayoutCalculator.AllDaySectionPadding + bar.Lane * (CalendarSurfaceLayoutCalculator.AllDayItemHeight + CalendarSurfaceLayoutCalculator.AllDayItemGap);

            foreach (var (first, last) in SplitAtBreak(bar.Span, breakIndex))
            {
                var bounds = new CGRect(first * dayWidth + AllDayColumnInset, y,
                    Math.Max(0, (last - first + 1) * dayWidth - AllDayBarInset), CalendarSurfaceLayoutCalculator.AllDayItemHeight);
                placements.Add(new CalendarItemPlacement(bar.Item, dates[first], bounds, CalendarPlacementKind.AllDay));
            }
        }

        var overflows = new List<CalendarOverflowCell>();
        var moreY = CalendarSurfaceLayoutCalculator.AllDayMoreRowY;

        for (var day = 0; day < dates.Count; day++)
        {
            if (hiddenByDay[day].Count == 0) continue;

            var bounds = new CGRect(day * dayWidth + AllDayColumnInset, moreY, Math.Max(0, dayWidth - AllDayBarInset), CalendarItemOverflow.MoreButtonHeight);
            overflows.Add(new CalendarOverflowCell(dates[day], hiddenByDay[day], bounds));
        }

        return (placements, overflows);
    }

    /// <summary>
    /// The month grid, one week row at a time: bars first on their lanes, the other events in the free slots of
    /// each day, and the slots that do not fit above the day's "n more" link. A bar that does not fit on one of its
    /// days moves to the overflow of all of them. Bars are split per week row, as Windows does.
    /// </summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateMonth(
        IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double width, double height)
    {
        var cellWidth = width <= 0 ? 0 : width / Columns;
        var cellHeight = height <= 0 ? 0 : height / CalendarSurfaceLayoutCalculator.MonthRows;

        var cells = new List<CalendarMonthCell>(dates.Count);
        for (var index = 0; index < dates.Count; index++)
        {
            var row = index / Columns;
            var column = index % Columns;
            cells.Add(new CalendarMonthCell(dates[index], index, row, column, new CGRect(column * cellWidth, row * cellHeight, cellWidth, cellHeight)));
        }

        var placements = new List<CalendarItemPlacement>();
        var overflows = new List<CalendarOverflowCell>();
        var allItems = items.ToList();

        for (var rowStart = 0; rowStart < dates.Count; rowStart += Columns)
        {
            var rowDates = dates.Skip(rowStart).Take(Columns).ToList();
            CalculateRow(rowDates, allItems, cellWidth, cellHeight, rowStart / Columns * cellHeight, placements, overflows);
        }

        return (cells, placements, overflows);
    }

    private static void CalculateRow(List<DateOnly> rowDates, List<ICalendarItem> items, double cellWidth, double cellHeight, double top,
        List<CalendarItemPlacement> placements, List<CalendarOverflowCell> overflows)
    {
        var columnCount = rowDates.Count;
        var stackTop = top + CalendarSurfaceLayoutCalculator.MonthDayLabelHeight + CalendarSurfaceLayoutCalculator.MonthCellPadding;
        var availableHeight = cellHeight - CalendarSurfaceLayoutCalculator.MonthCellPadding - (stackTop - top);
        var bars = AssignBars(rowDates, items);

        var slots = new List<Slot>[columnCount];
        for (var column = 0; column < columnCount; column++) slots[column] = new List<Slot>();

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            for (var column = bar.Span.FirstIndex; column <= bar.Span.LastIndex; column++)
                slots[column].Add(new Slot(bar.Lane, bar.Item, barIndex));
        }

        var cellItems = new List<ICalendarItem>[columnCount];
        for (var column = 0; column < columnCount; column++) cellItems[column] = new List<ICalendarItem>();

        foreach (var item in items)
        {
            if (ShowsInAllDayArea(item) || CalendarSpanLanes.GetSpan(rowDates, item.StartDate, item.EndDate) is not { } span) continue;
            for (var column = span.FirstIndex; column <= span.LastIndex; column++) cellItems[column].Add(item);
        }

        for (var column = 0; column < columnCount; column++)
        {
            var taken = new HashSet<int>(slots[column].Select(slot => slot.Lane));
            var nextSlot = 0;

            foreach (var item in cellItems[column])
            {
                while (taken.Contains(nextSlot)) nextSlot++;
                slots[column].Add(new Slot(nextSlot, item, -1));
                taken.Add(nextSlot);
            }
        }

        var visibleSlots = new int[columnCount];
        for (var column = 0; column < columnCount; column++)
        {
            var slotCount = slots[column].Count == 0 ? 0 : slots[column].Max(slot => slot.Lane) + 1;
            var heights = new double[slotCount];
            Array.Fill(heights, CalendarSurfaceLayoutCalculator.MonthItemHeight);
            visibleSlots[column] = CalendarItemOverflow.FitStack(heights, availableHeight, CalendarSurfaceLayoutCalculator.MonthItemGap, CalendarItemOverflow.MoreButtonHeight);
        }

        var hiddenBars = new bool[bars.Count];
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            for (var column = bar.Span.FirstIndex; column <= bar.Span.LastIndex; column++)
            {
                if (bar.Lane < visibleSlots[column]) continue;
                hiddenBars[barIndex] = true;
                break;
            }

            if (hiddenBars[barIndex]) continue;

            var bounds = new CGRect(
                bar.Span.FirstIndex * cellWidth + CalendarSurfaceLayoutCalculator.MonthCellPadding,
                stackTop + bar.Lane * MonthItemPitch,
                Math.Max(0, bar.Span.Length * cellWidth - CalendarSurfaceLayoutCalculator.MonthCellPadding * 2),
                CalendarSurfaceLayoutCalculator.MonthItemHeight);
            placements.Add(new CalendarItemPlacement(bar.Item, rowDates[bar.Span.FirstIndex], bounds, CalendarPlacementKind.MonthCell));
        }

        var slotsWithLink = Math.Max(0, (int)Math.Floor((availableHeight - CalendarItemOverflow.MoreButtonHeight) / MonthItemPitch));
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            if (!hiddenBars[barIndex]) continue;

            var span = bars[barIndex].Span;
            for (var column = span.FirstIndex; column <= span.LastIndex; column++)
                visibleSlots[column] = Math.Min(visibleSlots[column], slotsWithLink);
        }

        for (var column = 0; column < columnCount; column++)
        {
            var hidden = new List<ICalendarItem>();

            foreach (var slot in slots[column].OrderBy(entry => entry.Lane))
            {
                if (slot.BarIndex >= 0)
                {
                    if (hiddenBars[slot.BarIndex]) hidden.Add(slot.Item);
                    continue;
                }

                if (slot.Lane >= visibleSlots[column])
                {
                    hidden.Add(slot.Item);
                    continue;
                }

                var bounds = new CGRect(
                    column * cellWidth + CalendarSurfaceLayoutCalculator.MonthCellPadding,
                    stackTop + slot.Lane * MonthItemPitch,
                    Math.Max(0, cellWidth - CalendarSurfaceLayoutCalculator.MonthCellPadding * 2),
                    CalendarSurfaceLayoutCalculator.MonthItemHeight);
                placements.Add(new CalendarItemPlacement(slot.Item, rowDates[column], bounds, CalendarPlacementKind.MonthCell));
            }

            if (hidden.Count == 0) continue;

            var moreBounds = new CGRect(
                column * cellWidth + CalendarSurfaceLayoutCalculator.MonthCellPadding,
                top + Math.Max(0, cellHeight - CalendarSurfaceLayoutCalculator.MonthCellPadding - CalendarItemOverflow.MoreButtonHeight),
                Math.Max(0, cellWidth - CalendarSurfaceLayoutCalculator.MonthCellPadding * 2),
                CalendarItemOverflow.MoreButtonHeight);
            overflows.Add(new CalendarOverflowCell(rowDates[column], hidden, moreBounds));
        }
    }

    private static List<Bar> AssignBars(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(items);

        var candidates = new List<(ICalendarItem Item, CalendarDaySpan Span)>();

        foreach (var item in items
                     .Where(ShowsInAllDayArea)
                     .OrderBy(item => item.StartDate)
                     .ThenBy(item => item.Title, StringComparer.CurrentCulture))
        {
            if (CalendarSpanLanes.GetSpan(dates, item.StartDate, item.EndDate) is { } span)
                candidates.Add((item, span));
        }

        var lanes = CalendarSpanLanes.Assign(candidates.ConvertAll(candidate => candidate.Span));
        var bars = new List<Bar>(candidates.Count);

        for (var index = 0; index < candidates.Count; index++)
            bars.Add(new Bar(candidates[index].Item, candidates[index].Span, lanes[index]));

        return bars;
    }

    private static IEnumerable<(int First, int Last)> SplitAtBreak(CalendarDaySpan span, int breakIndex)
    {
        if (span.FirstIndex < breakIndex && breakIndex <= span.LastIndex)
        {
            yield return (span.FirstIndex, breakIndex - 1);
            yield return (breakIndex, span.LastIndex);
            yield break;
        }

        yield return (span.FirstIndex, span.LastIndex);
    }

    private static bool ShowsInAllDayArea(ICalendarItem item) => item.IsAllDayEvent || item.IsMultiDayEvent;

    private readonly record struct Bar(ICalendarItem Item, CalendarDaySpan Span, int Lane);

    private readonly record struct Slot(int Lane, ICalendarItem Item, int BarIndex);
}
