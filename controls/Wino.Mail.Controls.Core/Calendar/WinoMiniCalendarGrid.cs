namespace Wino.Mail.Controls.Core.Calendar;

/// <summary>
/// Month-grid math of the calendar pane's date picker (Windows ShellCalendarView): a header row, a
/// weekday-name row and six week rows of seven days starting on the configured first day of week.
/// Coordinates are top-down (flipped) and relative to the picker's bounds.
/// </summary>
public static class WinoMiniCalendarGrid
{
    public const double Padding = 10;
    public const double HeaderHeight = 22;
    public const double NamesHeight = 16;
    public const double NamesTopGap = 6;
    public const double CellHeight = 24;
    public const int Rows = 6;
    public const int Columns = 7;
    public const int CellCount = Rows * Columns;

    /// <summary>Picker height; only the header row shows while collapsed.</summary>
    public static double HeightFor(bool collapsed)
        => collapsed
            ? Padding + HeaderHeight + Padding
            : Padding + HeaderHeight + NamesTopGap + NamesHeight + Rows * CellHeight + Padding;

    /// <summary>Top of the weekday-name row.</summary>
    public static double NamesTop => Padding + HeaderHeight + NamesTopGap;

    /// <summary>Top of the first week row.</summary>
    public static double GridTop => NamesTop + NamesHeight;

    public static double ColumnWidth(double width) => Math.Max(0, width - Padding * 2) / Columns;

    /// <summary>Side of the header buttons (previous and next month, collapse toggle).</summary>
    public const double HeaderButtonSize = 16;

    /// <summary>Gap between the header buttons.</summary>
    public const double HeaderButtonSpacing = 8;

    /// <summary>Extra margin around a header button that still counts as a hit.</summary>
    public const double HeaderButtonHitSlop = 4;

    /// <summary>
    /// A header button's rectangle as (X, Y, Width, Height) in a picker <paramref name="width"/> wide: previous, next
    /// and the toggle sit right-aligned in that order, vertically centred in the header row.
    /// </summary>
    public static (double X, double Y, double Width, double Height) HeaderButtonBounds(WinoMiniCalendarHeaderButton button, double width)
    {
        int fromTrailing = button switch
        {
            WinoMiniCalendarHeaderButton.Previous => 2,
            WinoMiniCalendarHeaderButton.Next => 1,
            _ => 0
        };
        double x = width - Padding - HeaderButtonSize - fromTrailing * (HeaderButtonSize + HeaderButtonSpacing);
        return (x, Padding + (HeaderHeight - HeaderButtonSize) / 2, HeaderButtonSize, HeaderButtonSize);
    }

    /// <summary>The header button under a point (with <see cref="HeaderButtonHitSlop"/>), or null.</summary>
    public static WinoMiniCalendarHeaderButton? HitTestHeader(double x, double y, double width)
    {
        foreach (var button in (WinoMiniCalendarHeaderButton[])[WinoMiniCalendarHeaderButton.Toggle, WinoMiniCalendarHeaderButton.Previous, WinoMiniCalendarHeaderButton.Next])
        {
            var (left, top, buttonWidth, buttonHeight) = HeaderButtonBounds(button, width);
            if (x >= left - HeaderButtonHitSlop && x < left + buttonWidth + HeaderButtonHitSlop
                && y >= top - HeaderButtonHitSlop && y < top + buttonHeight + HeaderButtonHitSlop)
                return button;
        }

        return null;
    }

    /// <summary>Width left for the month title in the header row.</summary>
    public static double TitleWidth(double width) => Math.Max(0, width - Padding * 2 - 80);

    /// <summary>The first day of the month that contains <paramref name="date"/>.</summary>
    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>The first cell's date: the month's first day moved back to <paramref name="firstDayOfWeek"/>.</summary>
    public static DateOnly GridStart(DateOnly displayMonth, DayOfWeek firstDayOfWeek)
    {
        var first = MonthStart(displayMonth);
        int offset = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return first.AddDays(-offset);
    }

    /// <summary>The 42 dates the grid shows, row by row.</summary>
    public static DateOnly[] GridDates(DateOnly displayMonth, DayOfWeek firstDayOfWeek)
    {
        var start = GridStart(displayMonth, firstDayOfWeek);
        var dates = new DateOnly[CellCount];
        for (int index = 0; index < dates.Length; index++) dates[index] = start.AddDays(index);
        return dates;
    }

    /// <summary>The cell index of <paramref name="date"/>, or -1 when the grid does not show it.</summary>
    public static int CellIndex(DateOnly date, DateOnly displayMonth, DayOfWeek firstDayOfWeek)
    {
        int index = date.DayNumber - GridStart(displayMonth, firstDayOfWeek).DayNumber;
        return index is >= 0 and < CellCount ? index : -1;
    }

    /// <summary>The index into <see cref="System.Globalization.DateTimeFormatInfo.ShortestDayNames"/> for a column.</summary>
    public static int DayNameIndex(int column, DayOfWeek firstDayOfWeek) => ((int)firstDayOfWeek + column) % 7;

    /// <summary>A cell's rectangle as (X, Y, Width, Height) in a picker <paramref name="width"/> wide.</summary>
    public static (double X, double Y, double Width, double Height) CellBounds(int index, double width)
    {
        double columnWidth = ColumnWidth(width);
        return (Padding + index % Columns * columnWidth, GridTop + index / Columns * CellHeight, columnWidth, CellHeight);
    }

    /// <summary>The cell under a point, or -1 outside the week rows.</summary>
    public static int HitTest(double x, double y, double width)
    {
        double columnWidth = ColumnWidth(width);
        if (y < GridTop || columnWidth <= 0) return -1;

        int row = (int)((y - GridTop) / CellHeight);
        int column = (int)((x - Padding) / columnWidth);
        if (row < 0 || row >= Rows || column < 0 || column >= Columns) return -1;

        return row * Columns + column;
    }

    /// <summary>
    /// The focused day after the shown month changes: unchanged when it is already in the new month,
    /// otherwise the same day number clamped to the new month's length.
    /// </summary>
    public static DateOnly KeepFocusInMonth(DateOnly focused, DateOnly displayMonth)
    {
        var first = MonthStart(displayMonth);
        if (focused.Year == first.Year && focused.Month == first.Month) return focused;

        return first.AddDays(Math.Min(focused.Day, DateTime.DaysInMonth(first.Year, first.Month)) - 1);
    }

    /// <summary>
    /// Which ends of a selected day's highlight are rounded: a contiguous run is one band, rounded only at
    /// its first and last day and at the row edges.
    /// </summary>
    public static (bool RoundLeading, bool RoundTrailing) SelectionEnds(IReadOnlySet<DateOnly> selected, DateOnly date, int column)
    {
        ArgumentNullException.ThrowIfNull(selected);

        bool leading = column == 0 || !selected.Contains(date.AddDays(-1));
        bool trailing = column == Columns - 1 || !selected.Contains(date.AddDays(1));
        return (leading, trailing);
    }
}

/// <summary>The buttons in the date picker's header row.</summary>
public enum WinoMiniCalendarHeaderButton
{
    Previous,
    Next,
    Toggle
}
