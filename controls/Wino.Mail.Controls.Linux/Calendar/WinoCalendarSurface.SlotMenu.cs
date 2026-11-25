using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The context menu request of an empty slot: right click or a touch long press on the all-day strip, the hour grid or a
/// month cell, and Menu / Shift+F10 on the focused surface (the first visible slot). Tiles claim their own presses.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    partial void AttachSlotMenu()
    {
        foreach (var strip in (CalendarStrip[])[CalendarStrip.AllDay, CalendarStrip.Timed, CalendarStrip.Month])
        {
            var widget = StripWidget(strip);
            var target = strip;
            var click = Gtk.GestureClick.New();
            click.SetButton(3);
            widget.AddController(click);
            Own(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(h => click.OnPressed += h, h => click.OnPressed -= h, (gesture, args) =>
            {
                if (RaiseSlotContext(target, args.X, args.Y)) gesture.SetState(Gtk.EventSequenceState.Claimed);
            }));
            var longPress = Gtk.GestureLongPress.New();
            longPress.SetTouchOnly(true);
            widget.AddController(longPress);
            Own(GtkSignals.Connect<Gtk.GestureLongPress, Gtk.GestureLongPress.PressedSignalArgs>(h => longPress.OnPressed += h, h => longPress.OnPressed -= h,
                (_, args) => RaiseSlotContext(target, args.X, args.Y)));
        }
    }

    /// <summary>Raises <see cref="SlotContextRequested"/> for the slot under a point in strip coordinates; false over the gutter or outside the cells.</summary>
    internal bool RaiseSlotContext(CalendarStrip strip, double x, double y)
    {
        if (SlotAt(strip, x, y) is not { } args) return false;
        CancelPress();
        RaiseSlotContextRequested(args);
        return true;
    }

    /// <summary>The slot under a point in strip coordinates: a timed slot has a time, an all-day day or a month cell does not.</summary>
    private CalendarSlotEventArgs? SlotAt(CalendarStrip strip, double x, double y)
    {
        if (_space is null || _windowDates.Count == 0) return null;
        var (railX, railY) = ToRail(strip, x, y);
        DateTime? start = null;
        var isAllDay = true;
        switch (strip)
        {
            case CalendarStrip.Timed when x >= HourColumnWidth:
                start = WinoCalendarSurfaceGeometry.SlotStartAt(railX, railY, _windowDates, DayWidth, HourHeight);
                isAllDay = false;
                break;
            case CalendarStrip.AllDay when x >= HourColumnWidth && DayWidth > 0:
                start = _windowDates[WinoCalendarSurfaceGeometry.DayIndexAt(railX, _windowDates.Count, DayWidth)].ToDateTime(TimeOnly.MinValue);
                break;
            case CalendarStrip.Month:
                start = MonthCellAt(railX, railY);
                break;
        }
        if (start is not { } slot) return null;
        var anchor = new Gdk.Rectangle { X = (int)Math.Round(x), Y = (int)Math.Round(y), Width = 1, Height = 1 };
        return new CalendarSlotEventArgs(slot, null, isAllDay, StripWidget(strip), anchor);
    }

    /// <summary>Menu / Shift+F10 on the surface: the slot at the top of the first visible day (Month: its first visible cell).</summary>
    private bool RaiseKeyboardSlotContext()
    {
        if (KeyboardSlotPoint() is not { } point) return false;
        return RaiseSlotContext(point.Strip, point.X, point.Y);
    }

    /// <summary>Ctrl+V on the surface pastes into the keyboard slot.</summary>
    private bool RaiseKeyboardPaste()
    {
        if (KeyboardSlotPoint() is not { } point || SlotAt(point.Strip, point.X, point.Y) is not { } args) return false;
        PasteRequested?.Invoke(this, args);
        return true;
    }

    private (CalendarStrip Strip, double X, double Y)? KeyboardSlotPoint()
    {
        if (_space is null || _windowDates.Count == 0 || _unitSize <= 0) return null;
        if (IsMonth) return (CalendarStrip.Month, 4, 4);
        var unit = (int)Math.Ceiling(_position - PositionEpsilon);
        var index = unit - _windowStart;
        if (index < 0 || index >= _windowDates.Count) return null;
        var adjustment = _scroller.GetVadjustment();
        var slot = WinoCalendarSurfaceGeometry.SlotAt(adjustment.GetValue() + HourHeight / 2, HourHeight);
        var bounds = WinoCalendarSurfaceGeometry.SlotBounds(index, slot, DayWidth, HourHeight);
        var (x, y) = ToStrip(CalendarStrip.Timed, bounds.X, bounds.Y);
        return (CalendarStrip.Timed, x + bounds.Width / 2, y + bounds.Height / 2);
    }

#if DEBUG
    public bool DebugPasteAtKeyboardSlot() => RaiseKeyboardPaste();
#endif
}
