using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public sealed partial class CalendarPeriodControl
{
    private Pointer? _rangePointer;
    private DateTime _rangeAnchorDate;
    private Point _rangeAnchorPoint;
    private CalendarSelectionRange? _dragSelection;
    private bool _suppressRangeTap;

    public event EventHandler? RangeSelectionStarted;

    private CalendarSelectionRange? CurrentSelection => _dragSelection ??
        (SelectedDateTime is DateTime start
            ? new CalendarSelectionRange(start, SelectedEndDateTime ?? start.AddMinutes(TimedSelectionIntervalMinutes))
            : null);

    private bool TryBeginRangeSelection(PointerRoutedEventArgs e)
    {
        _suppressRangeTap = false;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
            return false;

        var layer = VisibleRange?.DisplayType == CalendarDisplayType.Month ? MonthInteractionLayer : TimedInteractionLayer;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != layer)
            source = VisualTreeHelper.GetParent(source);

        if (source == null || GetRangeCell(e) is not DateTime anchor)
            return false;

        RangeSelectionStarted?.Invoke(this, EventArgs.Empty);
        if (!CapturePointer(e.Pointer))
            return false;

        Focus(FocusState.Programmatic);
        _rangePointer = e.Pointer;
        _rangeAnchorDate = anchor;
        _rangeAnchorPoint = e.GetCurrentPoint(this).Position;
        _dragSelection = CalendarSelectionRange.FromCells(anchor, anchor, RangeCellDuration);
        _suppressRangeTap = true;
        InvalidateStructureCanvases();
        e.Handled = true;
        return true;
    }

    private TimeSpan RangeCellDuration => VisibleRange?.DisplayType == CalendarDisplayType.Month
        ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(TimedSelectionIntervalMinutes);

    private DateTime? GetRangeCell(PointerRoutedEventArgs e)
        => VisibleRange?.DisplayType == CalendarDisplayType.Month
            ? ResolveMonthDropTarget(e.GetCurrentPoint(MonthViewport).Position, null)?.TargetStart.Date
            : ResolveTimedDropTarget(e.GetCurrentPoint(TimedViewport).Position, null)?.TargetStart;

    private bool TryUpdateRangeSelection(PointerRoutedEventArgs e)
    {
        if (_rangePointer?.PointerId != e.Pointer.PointerId)
            return false;

        if (GetRangeCell(e) is DateTime current)
        {
            _dragSelection = CalendarSelectionRange.FromCells(_rangeAnchorDate, current, RangeCellDuration);
            InvalidateStructureCanvases();
        }

        e.Handled = true;
        return true;
    }

    private bool TryCompleteRangeSelection(PointerRoutedEventArgs e)
    {
        if (!TryUpdateRangeSelection(e))
            return false;

        var selection = _dragSelection;
        CancelRangeSelection();
        if (selection != null)
        {
            EmptySlotTapped?.Invoke(this, new CalendarEmptySlotTappedEventArgs(
                selection.Start, _rangeAnchorPoint, new Size(1, 1), selection.End));
        }

        return true;
    }

    private void CancelRangeSelection()
    {
        var pointer = _rangePointer;
        _rangePointer = null;
        _dragSelection = null;
        if (pointer != null)
            ReleasePointerCapture(pointer);

        InvalidateStructureCanvases();
    }

    private void SelectionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _rangePointer != null)
        {
            CancelRangeSelection();
            e.Handled = true;
        }
    }
}
