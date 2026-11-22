using System.Globalization;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.Layout;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// Range selection on empty grid space (macOS WinoCalendarSurfaceView.Selection, Windows CalendarPeriodControl.Selection):
/// a press that moves <see cref="SelectionDragThreshold"/> across the rail (or any distance with Shift) selects snapped
/// cells in the hour grid, spanning days, or whole days in the month grid. The range is drawn with a time chip while
/// dragging; release raises <see cref="SlotClicked"/> with the range end. <see cref="SelectionRange"/> keeps a range
/// highlighted while the quick event popover is open.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const double SelectionDragThreshold = WinoCalendarSurfaceGeometry.SelectionDragThreshold;
    private const double AutoScrollEdge = 24;
    private const double AutoScrollStep = 8;

    private CalendarSelectionRange? _selectionRange;
    private bool _rangeSelectionActive;

    private sealed class SelectionSession(bool month)
    {
        public bool Month { get; } = month;
        public bool Dragging;
        public CalendarSelectionRange? Range;
    }

    /// <summary>A range to keep highlighted (the quick event being created); null for none.</summary>
    public CalendarSelectionRange? SelectionRange
    {
        get => _selectionRange;
        set
        {
            if (Equals(_selectionRange, value)) return;
            var previous = DisplayedSelection;
            _selectionRange = value;
            QueueDrawSelection(previous, DisplayedSelection);
        }
    }

    /// <summary>True while an empty-slot drag is selecting a range.</summary>
    public bool IsSelectingRange => _press?.Tag is SelectionSession { Dragging: true };

    private CalendarSelectionRange? DisplayedSelection => _press?.Tag is SelectionSession { Dragging: true } session ? session.Range : _selectionRange;

    /// <summary>Drops a press or a range drag in progress without raising anything.</summary>
    public void CancelSlotSelection() => CancelPress();

    private TimeSpan CellDuration(bool month) => month ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(Math.Max(1, SnapMinutes));

    partial void OnSlotPressMoved(SlotPress press, double stripX, double stripY, ref bool handled)
    {
        if (press.AnchorCell is not { } anchor) return;
        var month = press.Strip == CalendarStrip.Month;
        if (press.Strip is not (CalendarStrip.Timed or CalendarStrip.Month)) return;
        if (press.Tag is not SelectionSession session)
        {
            var dx = stripX - press.DownX;
            var dy = stripY - press.DownY;
            if (Math.Abs(dx) < SelectionDragThreshold && Math.Abs(dy) < SelectionDragThreshold) return;
            var (along, across) = month ? (dy, dx) : (dx, dy);
            // Movement along the rail may still become a pan.
            if (!press.Shift && Math.Abs(along) >= Math.Abs(across)) return;
            session = new SelectionSession(month) { Dragging = true };
            press.Tag = session;
            _rangeSelectionActive = true;
        }
        handled = true;
        if (!month) AutoScroll(stripY);
        var previous = session.Range;
        if (CellAt(press.Strip, stripX, stripY) is { } cell) session.Range = CalendarSelectionRange.FromCells(anchor, cell, CellDuration(month));
        if (!Equals(previous, session.Range)) QueueDrawSelection(previous, session.Range);
    }

    partial void OnSlotPressReleased(SlotPress press, double stripX, double stripY, ref bool handled)
    {
        if (press.Tag is not SelectionSession { Dragging: true } session) return;
        handled = true;
        var ignored = false;
        OnSlotPressMoved(press, stripX, stripY, ref ignored);
        EndSelectionSession(session);
        if (session.Range is not { } range || press.AnchorCell is not { } anchorCell) return;
        var anchorDate = DateOnly.FromDateTime(anchorCell);
        WinoLayoutFrame? rail = session.Month
            ? MonthCellBounds(anchorDate) is { } cell ? new WinoLayoutFrame(cell.X, cell.Y, cell.Width, cell.Height) : null
            : TimedSegmentBounds(range, anchorDate);
        if (rail is not { } frame) return;
        var (x, y) = ToStrip(press.Strip, frame.X, frame.Y);
        var anchor = new Gdk.Rectangle
        {
            X = (int)Math.Round(x),
            Y = (int)Math.Round(y),
            Width = Math.Max(1, (int)Math.Round(frame.Width)),
            Height = Math.Max(1, (int)Math.Round(frame.Height))
        };
        RaiseSlotClicked(new CalendarSlotEventArgs(range.Start, range.End, session.Month, StripWidget(press.Strip), anchor));
    }

    partial void OnSlotPressCancelled(SlotPress press)
    {
        if (press.Tag is SelectionSession session) EndSelectionSession(session);
    }

    private void EndSelectionSession(SelectionSession session)
    {
        var shown = session.Dragging ? session.Range : null;
        session.Dragging = false;
        _rangeSelectionActive = false;
        if (!Equals(shown, _selectionRange)) QueueDrawSelection(shown, _selectionRange);
    }

    /// <summary>The cell under a strip point; the hour grid clamps it to the visible days.</summary>
    private DateTime? CellAt(CalendarStrip strip, double stripX, double stripY)
    {
        if (strip == CalendarStrip.Month)
        {
            var (monthX, monthY) = ToRail(strip, stripX, stripY);
            return MonthCellAt(monthX, monthY, clamp: true);
        }
        var width = _timedOverlay.GetWidth();
        var clampedX = Math.Clamp(stripX, HourColumnWidth, Math.Max(HourColumnWidth, width - 1));
        var (railX, railY) = ToRail(strip, clampedX, stripY);
        return WinoCalendarSurfaceGeometry.TimedCellAt(railX, railY, _windowDates, DayWidth, HourHeight, SnapMinutes);
    }

    /// <summary>Scrolls the hour grid while the pointer is within the edge band of its viewport.</summary>
    private void AutoScroll(double stripY)
    {
        var adjustment = _scroller.GetVadjustment();
        var page = adjustment.GetPageSize();
        if (page <= 0) return;
        var value = adjustment.GetValue();
        var viewportY = stripY - value;
        if (viewportY < AutoScrollEdge) adjustment.SetValue(Math.Max(adjustment.GetLower(), value - AutoScrollStep));
        else if (viewportY > page - AutoScrollEdge) adjustment.SetValue(Math.Min(adjustment.GetUpper() - page, value + AutoScrollStep));
    }

    /// <summary>The part of <paramref name="range"/> on <paramref name="date"/> in rail coordinates.</summary>
    private WinoLayoutFrame? TimedSegmentBounds(CalendarSelectionRange range, DateOnly date)
    {
        var index = WinoCalendarSurfaceGeometry.IndexOf(_windowDates, date);
        if (index < 0 || range.IntersectDay(date) is not { } segment) return null;
        return WinoCalendarSurfaceGeometry.SelectionSegmentBounds(index, segment.Start, segment.End, DayWidth, HourHeight);
    }

    /// <summary>Redraws the day columns an old or new range touches, or the month grid.</summary>
    private void QueueDrawSelection(CalendarSelectionRange? previous, CalendarSelectionRange? next)
    {
        if (IsMonth)
        {
            _monthGrid.QueueDraw();
            return;
        }
        foreach (var column in _timedColumns.Values)
        {
            if (Touches(previous, column.Date) || Touches(next, column.Date)) column.Area.QueueDraw();
        }

        static bool Touches(CalendarSelectionRange? range, DateOnly date) => range?.IntersectDay(date) is not null;
    }

    partial void DrawSelection(Cairo.Context cr, CalendarStrip strip)
    {
        if (DisplayedSelection is not { } range) return;
        var palette = Palette;
        if (strip == CalendarStrip.Month)
        {
            foreach (var cell in _monthCells)
            {
                var start = cell.Date.ToDateTime(TimeOnly.MinValue);
                if (start < range.Start.Date || start >= range.End) continue;
                var bounds = cell.Bounds;
                DrawSelectionRect(cr, palette, bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4);
            }
            return;
        }
        if (strip != CalendarStrip.Timed) return;
        WinoLayoutFrame? first = null;
        foreach (var date in _windowDates)
        {
            if (TimedSegmentBounds(range, date) is not { } frame) continue;
            first ??= frame;
            DrawSelectionRect(cr, palette, frame.X, frame.Y, frame.Width, frame.Height);
        }
        if (!IsSelectingRange || first is not { } chip) return;
        var text = _layouts.Get($"{TimeText(range.Start)} – {TimeText(range.End)}", CalendarTextStyle.TileTime);
        var chipWidth = text.Width + 12;
        var chipHeight = text.Height + 4;
        palette.Accent.Use(cr);
        WinoCalendarTile.RoundedRect(cr, chip.X + 4, chip.Y + 3, chipWidth, chipHeight, chipHeight / 2.0);
        cr.Fill();
        palette.AccentForeground.Use(cr);
        cr.MoveTo(chip.X + 10, chip.Y + 5);
        PangoCairo.Functions.ShowLayout(cr, text.Layout);
    }

    private static void DrawSelectionRect(Cairo.Context cr, CalendarPalette palette, double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        palette.Selection.Use(cr);
        WinoCalendarTile.RoundedRect(cr, x, y, width, height, 4);
        cr.Fill();
        palette.Accent.Use(cr);
        cr.LineWidth = 1;
        WinoCalendarTile.RoundedRect(cr, x + 0.5, y + 0.5, width - 1, height - 1, 4);
        cr.Stroke();
    }

    private string TimeText(DateTime value) => _settings is null ? value.ToString("t", CultureInfo.CurrentCulture) : _settings.GetTimeString(value.TimeOfDay);

#if DEBUG
    /// <summary>
    /// Drives a range drag through the press router: a press in the first cell, a Shift move to the second cell and,
    /// unless <paramref name="hold"/>, the release. Days are indexes of the visible period.
    /// </summary>
    public string DebugSelectRange(int fromDay, TimeSpan fromTime, int toDay, TimeSpan toTime, bool hold)
    {
        if (_space is not { } space || _windowDates.Count == 0) return "no rail";
        var firstVisible = (int)Math.Round(_position) - _windowStart;
        if (IsMonth)
        {
            var from = _windowDates[Math.Clamp(firstVisible * 7 + fromDay, 0, _windowDates.Count - 1)];
            var to = _windowDates[Math.Clamp(firstVisible * 7 + toDay, 0, _windowDates.Count - 1)];
            if (MonthCellBounds(from) is not { } a || MonthCellBounds(to) is not { } b) return "day not realized";
            var (ax, ay) = ToStrip(CalendarStrip.Month, a.X + a.Width / 2, a.Y + a.Height / 2);
            var (bx, by) = ToStrip(CalendarStrip.Month, b.X + b.Width / 2, b.Y + b.Height / 2);
            return DebugDrive(CalendarStrip.Month, ax, ay, bx, by, hold);
        }
        var dayWidth = DayWidth;
        var startIndex = space.SnapAtOrBefore((int)Math.Round(_position)) - _windowStart;
        double X(int day) => HourColumnWidth + (startIndex + day) * dayWidth + dayWidth / 2 - RailOffset;
        double Y(TimeSpan time) => WinoCalendarSurfaceGeometry.TimeY(time, HourHeight) + 2;
        return DebugDrive(CalendarStrip.Timed, X(fromDay), Y(fromTime), X(toDay), Y(toTime - TimeSpan.FromMinutes(SnapMinutes)), hold);
    }

    private string DebugDrive(CalendarStrip strip, double x0, double y0, double x1, double y1, bool hold)
    {
        BeginPress(strip, x0, y0, shift: true);
        UpdatePress(null, (x1 - x0) / 2, (y1 - y0) / 2 + SelectionDragThreshold, 0);
        UpdatePress(null, x1 - x0, y1 - y0, 0);
        if (!hold) EndPress(x1 - x0, y1 - y0, 0);
        return DescribeSelection();
    }

    /// <summary>Raises <see cref="SlotClicked"/> as a release of a range drag over these times would.</summary>
    public string DebugRaiseSlot(DateTime start, DateTime end, bool allDay)
    {
        var date = DateOnly.FromDateTime(start);
        WinoLayoutFrame? frame;
        CalendarStrip strip;
        if (IsMonth)
        {
            strip = CalendarStrip.Month;
            frame = MonthCellBounds(date) is { } cell ? new WinoLayoutFrame(cell.X, cell.Y, cell.Width, cell.Height) : null;
        }
        else if (allDay)
        {
            strip = CalendarStrip.AllDay;
            var index = WinoCalendarSurfaceGeometry.IndexOf(_windowDates, date);
            frame = index < 0 ? null : new WinoLayoutFrame(WinoCalendarSurfaceGeometry.DayX(index, DayWidth), 0, DayWidth, Math.Max(1, _allDayOverlay.GetHeight()));
        }
        else
        {
            strip = CalendarStrip.Timed;
            frame = TimedSegmentBounds(new CalendarSelectionRange(start, end), date);
        }
        if (frame is not { } bounds) return "day not realized";
        var (x, y) = ToStrip(strip, bounds.X, bounds.Y);
        var anchor = new Gdk.Rectangle { X = (int)Math.Round(x), Y = (int)Math.Round(y), Width = Math.Max(1, (int)Math.Round(bounds.Width)), Height = Math.Max(1, (int)Math.Round(bounds.Height)) };
        RaiseSlotClicked(new CalendarSlotEventArgs(start, end, allDay || IsMonth, StripWidget(strip), anchor));
        return $"raised {strip} ({anchor.X},{anchor.Y},{anchor.Width},{anchor.Height})";
    }

    public string DescribeSelection()
        => string.Create(CultureInfo.InvariantCulture,
            $"selection={(_selectionRange is { } s ? $"{s.Start:yyyy-MM-dd HH:mm}..{s.End:yyyy-MM-dd HH:mm}" : "-")} displayed={(DisplayedSelection is { } d ? $"{d.Start:yyyy-MM-dd HH:mm}..{d.End:yyyy-MM-dd HH:mm}" : "-")} selecting={IsSelectingRange} active={_rangeSelectionActive} gliding={_isGliding} panning={_isPanning}");
#endif
}
