using System;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.WinUI.Controls.Calendar;

public enum CalendarDropTargetKind
{
    TimedSlot,
    TimedAllDay,
    MonthCell
}

public sealed class CalendarItemDroppedEventArgs : EventArgs
{
    public CalendarItemDroppedEventArgs(
        CalendarItemViewModel calendarItemViewModel,
        DateTime targetStart,
        CalendarDropTargetKind targetKind)
    {
        CalendarItemViewModel = calendarItemViewModel;
        TargetStart = targetStart;
        TargetKind = targetKind;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
    public DateTime TargetStart { get; }
    public CalendarDropTargetKind TargetKind { get; }
}
