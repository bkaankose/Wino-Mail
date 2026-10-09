using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>
/// Drag across empty slots to pick a quick-event range (Windows CalendarPeriodControl.Selection).
/// A press that moves less than <see cref="SelectionDragThreshold"/> is a slot click, as before.
/// A drag selects <see cref="DragSnapMinutes"/> cells in the hour grid (spanning days) or whole days
/// in the month grid, with an accent highlight and a time chip. Esc cancels; releasing outside the
/// grid keeps the last valid cell. On release <see cref="SlotClicked"/> carries the range end.
/// <see cref="SelectionRange"/> keeps a range highlighted, for example while the quick-event popover is open.
/// The all-day strip stays click-only, like Windows.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    private const double SelectionDragThreshold = 3;
    private const ushort EscapeKeyCode = 53;

    private CalendarSelectionRange? _selectionRange;
    private SlotPress? _slotPress;

    private static NSColor SelectionFill => WinoStyle.Accent.ColorWithAlphaComponent((nfloat)0.18);

    /// <summary>A range to keep highlighted (the quick event being created); null for none.</summary>
    public CalendarSelectionRange? SelectionRange
    {
        get => _selectionRange;
        set
        {
            if (Equals(_selectionRange, value)) return;
            _selectionRange = value;
            _timed.NeedsDisplay = true;
            _month.NeedsDisplay = true;
        }
    }

    /// <summary>True while an empty-slot drag is selecting a range.</summary>
    public bool IsSelectingRange => _slotPress?.Dragging == true;

    private CalendarSelectionRange? DisplayedSelection => _slotPress is { Dragging: true } press ? press.Range : _selectionRange;

    /// <summary>One press on empty grid space: a click, or a range drag once it moves.</summary>
    private sealed class SlotPress(NSView host, bool month, DateTime anchorCell, CGPoint downPoint, Action click)
    {
        public NSView Host { get; } = host;
        public bool Month { get; } = month;
        public DateTime AnchorCell { get; } = anchorCell;
        public CGPoint DownPoint { get; } = downPoint;
        public Action Click { get; } = click;
        public bool Dragging { get; set; }
        public CalendarSelectionRange? Range { get; set; }
        public NSObject? KeyMonitor { get; set; }
    }

    private TimeSpan CellDuration(bool month) => month ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(Math.Max(1, DragSnapMinutes));

    /// <summary>The canvas got a press on empty space; <paramref name="click"/> runs if it never becomes a drag.</summary>
    internal void BeginSlotPress(NSView host, bool month, CGPoint point, Action click)
    {
        CancelSlotSelection();
        var cell = month ? _month.CellAt(point) : TimedCellAt(point);
        if (cell is null) { click(); return; }
        _slotPress = new SlotPress(host, month, cell.Value, point, click);
    }

    internal void DragSlotPress(NSEvent native)
    {
        if (_slotPress is not { } press) return;
        var point = press.Host.ConvertPointFromView(native.LocationInWindow, null);
        if (!press.Dragging)
        {
            if (Math.Abs(point.X - press.DownPoint.X) < SelectionDragThreshold && Math.Abs(point.Y - press.DownPoint.Y) < SelectionDragThreshold) return;
            press.Dragging = true;
            // Esc cancels the drag; the monitor lives only while the drag does.
            press.KeyMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, keyEvent =>
            {
                if (keyEvent.KeyCode != EscapeKeyCode || _slotPress is null) return keyEvent;
                CancelSlotSelection();
                return null!;
            });
        }
        if (!press.Month) press.Host.Autoscroll(native);
        var current = press.Month ? _month.CellAt(point, clamp: true) : TimedCellAt(point);
        if (current is { } cell) press.Range = CalendarSelectionRange.FromCells(press.AnchorCell, cell, CellDuration(press.Month));
        press.Host.NeedsDisplay = true;
    }

    internal void EndSlotPress(NSEvent native)
    {
        if (_slotPress is not { } press) return;
        if (!press.Dragging)
        {
            _slotPress = null;
            press.Click();
            return;
        }
        DragSlotPress(native);
        var range = press.Range;
        EndSlotSession();
        if (range is null) return;
        var anchor = press.Month ? _month.CellBounds(DateOnly.FromDateTime(press.AnchorCell)) : TimedSegmentBounds(range, DateOnly.FromDateTime(press.AnchorCell));
        if (anchor is not { } rect) return;
        SlotClicked?.Invoke(this, new CalendarSlotClickedEventArgs(range.Start, ConvertRectFromView(rect, press.Host), press.Month, range.End));
    }

    /// <summary>Drops a press or drag in progress without raising anything.</summary>
    public void CancelSlotSelection()
    {
        if (_slotPress is null) return;
        EndSlotSession();
    }

    private void EndSlotSession()
    {
        var press = _slotPress;
        _slotPress = null;
        if (press?.KeyMonitor is { } monitor) NSEvent.RemoveMonitor(monitor);
        if (press is not null) press.Host.NeedsDisplay = true;
    }

    public override void ViewWillMoveToWindow(NSWindow? newWindow)
    {
        if (newWindow is null) CancelSlotSelection();
        base.ViewWillMoveToWindow(newWindow);
    }

    /// <summary>The snap cell under <paramref name="point"/> in the hour grid, clamped to the visible days and the day's last cell.</summary>
    private DateTime? TimedCellAt(CGPoint point)
    {
        var dates = Dates;
        double dayWidth = DayWidth;
        double hourHeight = HourHeight;
        if (dates.Count == 0 || dayWidth <= 0 || hourHeight <= 0) return null;
        int dayIndex = Math.Clamp((int)Math.Floor((point.X - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
        int snap = Math.Max(1, DragSnapMinutes);
        double minutes = (point.Y - TimelinePadding) / hourHeight * 60;
        int cell = Math.Clamp((int)Math.Floor(minutes / snap), 0, 24 * 60 / snap - 1);
        return dates[dayIndex].ToDateTime(TimeOnly.MinValue).AddMinutes(cell * snap);
    }

    /// <summary>The part of <paramref name="range"/> on <paramref name="date"/> in hour-grid coordinates.</summary>
    private CGRect? TimedSegmentBounds(CalendarSelectionRange range, DateOnly date)
    {
        var dates = Dates;
        int index = -1;
        for (int candidate = 0; candidate < dates.Count; candidate++) if (dates[candidate] == date) index = candidate;
        if (index < 0 || range.IntersectDay(date) is not { } segment) return null;
        double dayWidth = DayWidth, hourHeight = HourHeight;
        double top = TimelinePadding + segment.Start.TimeOfDay.TotalHours * hourHeight;
        double height = (segment.End - segment.Start).TotalHours * hourHeight;
        return new CGRect(HourColumnWidth + index * dayWidth + 1, top, Math.Max(0, dayWidth - 3), Math.Max(2, height));
    }

    /// <summary>Hour grid: the selection in each day column, and a time chip while dragging.</summary>
    internal void DrawTimedSelection()
    {
        if (DisplayedSelection is not { } range) return;
        CGRect? first = null;
        foreach (var date in Dates)
        {
            if (TimedSegmentBounds(range, date) is not { } rect) continue;
            first ??= rect;
            var path = NSBezierPath.FromRoundedRect(rect, 4, 4);
            SelectionFill.SetFill();
            path.Fill();
            WinoStyle.Accent.SetStroke();
            path.LineWidth = 1;
            path.Stroke();
        }
        if (_slotPress is { Dragging: true } && first is { } chipRect)
        {
            string text = $"{TimeText(range.Start)} – {TimeText(range.End)}";
            DrawChip(text, new CGPoint(chipRect.X + 4, chipRect.Y + 3));
        }
    }

    /// <summary>Month grid: the selected day cells.</summary>
    internal void DrawMonthSelection(IReadOnlyList<CalendarMonthCell> cells)
    {
        if (DisplayedSelection is not { } range) return;
        foreach (var cell in cells)
        {
            var start = cell.Date.ToDateTime(TimeOnly.MinValue);
            if (start < range.Start.Date || start >= range.End) continue;
            var rect = cell.Bounds.Inset(2, 2);
            var path = NSBezierPath.FromRoundedRect(rect, 4, 4);
            SelectionFill.SetFill();
            path.Fill();
            WinoStyle.Accent.SetStroke();
            path.LineWidth = 1;
            path.Stroke();
        }
    }

    private string TimeText(DateTime value) => _settings is null ? value.ToString("t") : _settings.GetTimeString(value.TimeOfDay);

    private static void DrawChip(string text, CGPoint origin)
    {
        var font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold);
        var attributed = new NSAttributedString(text, new NSStringAttributes { Font = font, ForegroundColor = NSColor.White });
        var size = attributed.Size;
        var rect = new CGRect(origin.X, origin.Y, Math.Ceiling(size.Width) + 10, Math.Ceiling(size.Height) + 2);
        WinoStyle.Hex(0x000000, 0.78).SetFill();
        NSBezierPath.FromRoundedRect(rect, 4, 4).Fill();
        attributed.DrawAtPoint(new CGPoint(rect.X + 5, rect.Y + 1));
    }
}
