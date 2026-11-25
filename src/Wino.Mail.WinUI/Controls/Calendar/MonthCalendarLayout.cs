using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.WinUI.Controls.Calendar;

/// <summary>
/// The placement of one calendar item inside a month week row. Bounds are relative to the row.
/// A bar (<paramref name="IsSpan"/>) starts in <paramref name="Column"/> and may cover the following columns.
/// </summary>
internal readonly record struct MonthItemLayout(CalendarItemViewModel Item, int Column, DateOnly Date, LayoutRect Bounds, bool IsSpan);

internal readonly record struct MonthRowLayout(List<MonthItemLayout> Items, List<CalendarOverflowLayout> Overflows);

internal static class MonthCalendarLayoutCalculator
{
    public const int ColumnCount = 7;
    public const int RowCount = 6;

    private const double CellPadding = 4d;
    private const double DayLabelHeight = 20d;

    // 22 px rather than the 21 px a title line needs keeps descenders inside the tile.
    private const double ItemHeight = 22d;
    private const double ItemGap = 2d;

    /// <summary>
    /// Lays out the items of one week row. All-day events and timed events of 24 hours or more are
    /// bars on lanes across the days they cover; the other events fill the free slots of each day
    /// under them. Slots that do not fit go to the day's overflow link; a bar that does not fit on
    /// one of its days is moved to the overflow of all of them.
    /// </summary>
    public static MonthRowLayout CalculateRow(IReadOnlyList<DateOnly> dates, IEnumerable<CalendarItemViewModel> items, double cellWidth, double cellHeight)
    {
        var itemLayouts = new List<MonthItemLayout>();
        var overflows = new List<CalendarOverflowLayout>();

        if (cellWidth <= 0d || cellHeight <= 0d || dates.Count == 0)
        {
            return new MonthRowLayout(itemLayouts, overflows);
        }

        var stackTop = DayLabelHeight + CellPadding;
        var availableHeight = cellHeight - CellPadding - stackTop;
        var columnCount = dates.Count;

        var spanItems = new List<CalendarItemViewModel>();
        var spans = new List<CalendarDaySpan>();
        var cellItems = new List<CalendarItemViewModel>[columnCount];

        for (var column = 0; column < columnCount; column++)
        {
            cellItems[column] = [];
        }

        foreach (var item in items)
        {
            if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end) ||
                CalendarSpanLanes.GetSpan(dates, start.LocalDateTime, end.LocalDateTime) is not CalendarDaySpan span)
            {
                continue;
            }

            if (item.ShowsInAllDayArea)
            {
                spanItems.Add(item);
                spans.Add(span);
                continue;
            }

            for (var column = span.FirstIndex; column <= span.LastIndex; column++)
            {
                cellItems[column].Add(item);
            }
        }

        var lanes = CalendarSpanLanes.Assign(spans);

        // Slot occupancy per day: the bars' lanes first, then the other events in the free slots.
        var cellSlots = new List<(int Slot, CalendarItemViewModel Item, int SpanIndex)>[columnCount];

        for (var column = 0; column < columnCount; column++)
        {
            cellSlots[column] = [];
        }

        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            for (var column = spans[spanIndex].FirstIndex; column <= spans[spanIndex].LastIndex; column++)
            {
                cellSlots[column].Add((lanes[spanIndex], spanItems[spanIndex], spanIndex));
            }
        }

        for (var column = 0; column < columnCount; column++)
        {
            var taken = new HashSet<int>(cellSlots[column].Select(entry => entry.Slot));
            var nextSlot = 0;

            foreach (var item in cellItems[column])
            {
                while (taken.Contains(nextSlot))
                {
                    nextSlot++;
                }

                cellSlots[column].Add((nextSlot, item, -1));
                taken.Add(nextSlot);
            }
        }

        // How many slots each day shows: all of them when they fit, otherwise as many as fit above the link.
        var visibleSlots = new int[columnCount];

        for (var column = 0; column < columnCount; column++)
        {
            var slotCount = cellSlots[column].Count == 0 ? 0 : cellSlots[column].Max(entry => entry.Slot) + 1;
            var heights = new double[slotCount];
            Array.Fill(heights, ItemHeight);

            visibleSlots[column] = CalendarItemOverflow.FitStack(heights, availableHeight, ItemGap, CalendarItemOverflow.MoreButtonHeight);
        }

        var hiddenSpans = new bool[spans.Count];
        var slotsWithLink = Math.Max(0, (int)Math.Floor((availableHeight - CalendarItemOverflow.MoreButtonHeight) / (ItemHeight + ItemGap)));

        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            for (var column = spans[spanIndex].FirstIndex; column <= spans[spanIndex].LastIndex; column++)
            {
                if (lanes[spanIndex] >= visibleSlots[column])
                {
                    hiddenSpans[spanIndex] = true;
                    break;
                }
            }

            if (hiddenSpans[spanIndex])
            {
                continue;
            }

            var span = spans[spanIndex];

            itemLayouts.Add(new MonthItemLayout(
                spanItems[spanIndex],
                span.FirstIndex,
                dates[span.FirstIndex],
                new LayoutRect(
                    (span.FirstIndex * cellWidth) + CellPadding,
                    GetSlotY(stackTop, lanes[spanIndex]),
                    Math.Max(0, (span.Length * cellWidth) - (CellPadding * 2)),
                    ItemHeight),
                IsSpan: true));
        }

        // A day that lost a bar to another day's overflow shows its own link, so it makes room for it.
        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            if (!hiddenSpans[spanIndex])
            {
                continue;
            }

            for (var column = spans[spanIndex].FirstIndex; column <= spans[spanIndex].LastIndex; column++)
            {
                visibleSlots[column] = Math.Min(visibleSlots[column], slotsWithLink);
            }
        }

        for (var column = 0; column < columnCount; column++)
        {
            var hiddenItems = new List<CalendarItemViewModel>();

            foreach (var (slot, item, spanIndex) in cellSlots[column].OrderBy(entry => entry.Slot))
            {
                if (spanIndex >= 0)
                {
                    if (hiddenSpans[spanIndex])
                    {
                        hiddenItems.Add(item);
                    }

                    continue;
                }

                if (slot >= visibleSlots[column])
                {
                    hiddenItems.Add(item);
                    continue;
                }

                itemLayouts.Add(new MonthItemLayout(
                    item,
                    column,
                    dates[column],
                    new LayoutRect(
                        (column * cellWidth) + CellPadding,
                        GetSlotY(stackTop, slot),
                        Math.Max(0, cellWidth - (CellPadding * 2)),
                        ItemHeight),
                    IsSpan: false));
            }

            if (hiddenItems.Count > 0)
            {
                overflows.Add(new CalendarOverflowLayout(
                    dates[column],
                    column,
                    hiddenItems,
                    GetMoreButtonBounds(column, cellWidth, cellHeight)));
            }
        }

        return new MonthRowLayout(itemLayouts, overflows);
    }

    public static LayoutRect GetMoreButtonBounds(int column, double cellWidth, double cellHeight)
        => new(
            (column * cellWidth) + CellPadding,
            Math.Max(0d, cellHeight - CellPadding - CalendarItemOverflow.MoreButtonHeight),
            Math.Max(0d, cellWidth - (CellPadding * 2)),
            CalendarItemOverflow.MoreButtonHeight);

    private static double GetSlotY(double stackTop, int slot) => stackTop + (slot * (ItemHeight + ItemGap));
}
