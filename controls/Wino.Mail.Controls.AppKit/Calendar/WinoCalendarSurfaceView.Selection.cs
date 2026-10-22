using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>
/// Presses on empty surface space (Windows CalendarPeriodControl.Selection and .Input). A press that
/// moves <see cref="PanStartThreshold"/> along the rail axis, more than across it, pans the rail and
/// glides to a period on release. Otherwise a press that moves less than
/// <see cref="SelectionDragThreshold"/> is a slot click, and a drag across the rail axis (or any
/// drag with Shift, as on Windows) picks a quick-event range: <see cref="DragSnapMinutes"/> cells in
/// the hour grid (spanning days) or whole days in the month grid, with an accent highlight and a
/// time chip. Esc cancels; releasing outside the grid keeps the last valid cell. On release
/// <see cref="SlotClicked"/> carries the range end. <see cref="SelectionRange"/> keeps a range
/// highlighted, for example while the quick-event popover is open. The day header and the all-day
/// strip only pan; the all-day strip clicks on release.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    private const double SelectionDragThreshold = WinoCalendarSurfaceGeometry.SelectionDragThreshold;
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
            _timedRail.NeedsDisplay = true;
            _monthRail.NeedsDisplay = true;
        }
    }

    /// <summary>True while an empty-slot drag is selecting a range.</summary>
    public bool IsSelectingRange => _slotPress?.Dragging == true;

    private CalendarSelectionRange? DisplayedSelection => _slotPress is { Dragging: true } press ? press.Range : _selectionRange;

    /// <summary>One press on empty surface space: a click, a pan once it moves along the rail, or a range drag.</summary>
    private sealed class SlotPress(NSView host, bool month, DateTime? anchorCell, CGPoint downPoint, CGPoint downInWindow, bool shift, Action? click)
    {
        public NSView Host { get; } = host;
        public bool Month { get; } = month;

        /// <summary>The cell a range drag starts from; null where the press can only click or pan (header, all-day strip).</summary>
        public DateTime? AnchorCell { get; } = anchorCell;
        public CGPoint DownPoint { get; } = downPoint;
        public CGPoint DownInWindow { get; } = downInWindow;

        /// <summary>Shift with the press always selects a range, as on Windows.</summary>
        public bool Shift { get; } = shift;
        public Action? Click { get; } = click;
        public bool Dragging { get; set; }
        public bool Panning { get; set; }
        public CalendarSelectionRange? Range { get; set; }
        public NSObject? KeyMonitor { get; set; }
    }

    private TimeSpan CellDuration(bool month) => month ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(Math.Max(1, DragSnapMinutes));

    /// <summary>The grid got a press on empty space; <paramref name="click"/> runs if it never becomes a pan or a drag.</summary>
    internal void BeginSlotPress(NSView host, bool month, CGPoint point, NSEvent native, Action click)
    {
        CancelSlotSelection();
        FocusSurface();
        var cell = month ? _monthRail.CellAt(point) : TimedCellAt(point);
        bool shift = native.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask);
        _slotPress = new SlotPress(host, month, cell, point, native.LocationInWindow, shift, click);
    }

    /// <summary>A press that can only pan the rail or click (the day header and the all-day strip).</summary>
    internal void BeginRailPress(NSView host, NSEvent native, Action? click)
    {
        CancelSlotSelection();
        FocusSurface();
        var point = host.ConvertPointFromView(native.LocationInWindow, null);
        _slotPress = new SlotPress(host, IsMonth, null, point, native.LocationInWindow, shift: false, click);
    }

    internal void DragSlotPress(NSEvent native)
    {
        if (_slotPress is not { } press) return;
        var (along, across) = AxisDeltas(press.DownInWindow, native.LocationInWindow);
        if (press.Panning)
        {
            UpdatePan(along, native.Timestamp);
            return;
        }
        var point = press.Host.ConvertPointFromView(native.LocationInWindow, null);
        if (!press.Dragging)
        {
            if (!press.Shift && _space is not null && _unitSize > 0 && Math.Abs(along) >= PanStartThreshold && Math.Abs(along) > Math.Abs(across))
            {
                press.Panning = true;
                BeginPan(along, native.Timestamp);
                return;
            }
            if (press.AnchorCell is null) return;
            if (Math.Abs(point.X - press.DownPoint.X) < SelectionDragThreshold && Math.Abs(point.Y - press.DownPoint.Y) < SelectionDragThreshold) return;
            // Movement along the rail may still become a pan.
            if (!press.Shift && Math.Abs(along) >= Math.Abs(across)) return;
            press.Dragging = true;
            // Esc cancels the drag; the monitor lives only while the drag does.
            press.KeyMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, keyEvent =>
            {
                if (keyEvent.KeyCode != EscapeKeyCode || _slotPress is null) return keyEvent;
                CancelSlotSelection();
                return null!;
            });
        }
        if (press.AnchorCell is not { } anchor) return;
        if (!press.Month) press.Host.Autoscroll(native);
        var current = press.Month ? _monthRail.CellAt(point, clamp: true) : TimedCellAt(point);
        if (current is { } cell) press.Range = CalendarSelectionRange.FromCells(anchor, cell, CellDuration(press.Month));
        press.Host.NeedsDisplay = true;
    }

    internal void EndSlotPress(NSEvent native)
    {
        if (_slotPress is not { } press) return;
        if (press.Panning)
        {
            _slotPress = null;
            EndPan(AxisDeltas(press.DownInWindow, native.LocationInWindow).Along, native.Timestamp);
            return;
        }
        if (!press.Dragging)
        {
            _slotPress = null;
            press.Click?.Invoke();
            return;
        }
        DragSlotPress(native);
        var range = press.Range;
        EndSlotSession();
        if (range is null || press.AnchorCell is not { } anchorCell) return;
        var anchor = press.Month ? _monthRail.CellBounds(DateOnly.FromDateTime(anchorCell)) : TimedSegmentBounds(range, DateOnly.FromDateTime(anchorCell));
        if (anchor is not { } rect) return;
        SlotClicked?.Invoke(this, new CalendarSlotClickedEventArgs(range.Start, ConvertRectFromView(rect, press.Host), press.Month, range.End));
    }

    /// <summary>Drops a press or drag in progress without raising anything.</summary>
    public void CancelSlotSelection()
    {
        if (_slotPress is null) return;
        bool wasPanning = _slotPress.Panning;
        EndSlotSession();
        if (!wasPanning) return;
        // An interrupted pan still comes to rest on a period.
        CancelPan();
        if (_space is { } space) GlideTo(CalendarRailMotion.ChooseSnapUnit(space, _position, 0), GlideDurationMilliseconds);
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
        if (newWindow is null)
        {
            CancelSlotSelection();
            // A glide in flight lands at once, so the rail and the loaded period stay in step.
            bool wasGliding = _isGliding;
            int glideTarget = _glideTarget;
            StopAllMotion();
            if (wasGliding)
            {
                SetRailPosition(glideTarget);
                SettleRail(glideTarget);
            }
            // The display link belongs to the old screen; a new one is made on the next glide.
            ReleaseFrameDriver();
        }
        base.ViewWillMoveToWindow(newWindow);
    }

    /// <summary>The snap cell under <paramref name="point"/> in the hour grid, clamped to the visible days and the day's last cell.</summary>
    private DateTime? TimedCellAt(CGPoint point)
    {
        point = new CGPoint(_timedRail.ClampToVisible(point).X, point.Y);
        return WinoCalendarSurfaceGeometry.TimedCellAt(point.X, point.Y, Dates, DayWidth, HourHeight, DragSnapMinutes);
    }

    /// <summary>The part of <paramref name="range"/> on <paramref name="date"/> in hour-grid coordinates.</summary>
    private CGRect? TimedSegmentBounds(CalendarSelectionRange range, DateOnly date)
    {
        int index = WinoCalendarSurfaceGeometry.IndexOf(Dates, date);
        if (index < 0 || range.IntersectDay(date) is not { } segment) return null;
        return ToRect(WinoCalendarSurfaceGeometry.SelectionSegmentBounds(index, segment.Start, segment.End, DayWidth, HourHeight));
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
