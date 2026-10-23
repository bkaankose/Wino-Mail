using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Calendar.Controls;

/// <summary>
/// Converts viewport points to rail points, then to a unit, a date and a slot. Taps, range
/// selection, hover and drag-and-drop all resolve their targets here.
/// </summary>
public sealed partial class CalendarPeriodControl
{
    private const double ViewportEdgeEpsilon = 0.5d;

    private void TimedInteractionLayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressRangeTap)
        {
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(TimedViewport);

        if (ResolveTimedDropTarget(position, null) is not CalendarDropTargetInfo target)
        {
            return;
        }

        var anchorPoint = TimedViewport.TransformToVisual(Root).TransformPoint(position);

        EmptySlotTapped?.Invoke(
            this,
            new CalendarEmptySlotTappedEventArgs(
                target.TargetStart,
                anchorPoint,
                new Size(_unitSize, GetTimedSelectionIntervalHeight())));
    }

    private void MonthInteractionLayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressRangeTap)
        {
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(MonthViewport);

        if (!TryResolveMonthCell(position, out _, out var date))
        {
            return;
        }

        var anchorPoint = MonthViewport.TransformToVisual(Root).TransformPoint(position);

        EmptySlotTapped?.Invoke(
            this,
            new CalendarEmptySlotTappedEventArgs(
                date.ToDateTime(TimeOnly.MinValue),
                anchorPoint,
                new Size(_monthCellWidth, _unitSize)));
    }

    private void TimedViewportPointerMoved(object sender, PointerRoutedEventArgs e)
        => UpdateHoverTargetForActiveDrag(() => ResolveTimedDropTarget(e.GetCurrentPoint(TimedViewport).Position, _activeDragPackage?.CalendarItemViewModel));

    private void TimedAllDayHostPointerMoved(object sender, PointerRoutedEventArgs e)
        => UpdateHoverTargetForActiveDrag(() => ResolveTimedAllDayDropTarget(e.GetCurrentPoint(TimedAllDayHost).Position, _activeDragPackage?.CalendarItemViewModel));

    private void MonthViewportPointerMoved(object sender, PointerRoutedEventArgs e)
        => UpdateHoverTargetForActiveDrag(() => ResolveMonthDropTarget(e.GetCurrentPoint(MonthViewport).Position, _activeDragPackage?.CalendarItemViewModel));

    private void CalendarDropTargetPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_activeDragPackage == null)
        {
            SetHoverTarget(null);
        }
    }

    private void UpdateHoverTargetForActiveDrag(Func<CalendarDropTargetInfo?> resolveHoverTarget)
    {
        if (_activeDragPackage == null)
        {
            SetHoverTarget(null);
            return;
        }

        SetHoverTarget(resolveHoverTarget());
    }

    private void TimedViewportDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDragPackage(e, out var dragPackage))
        {
            return;
        }

        var hoverTarget = ResolveTimedDropTarget(e.GetPosition(TimedViewport), dragPackage.CalendarItemViewModel);

        UpdateDragOverState(e, dragPackage, hoverTarget);
    }

    private void TimedAllDayHostDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDragPackage(e, out var dragPackage))
        {
            return;
        }

        var hoverTarget = ResolveTimedAllDayDropTarget(e.GetPosition(TimedAllDayHost), dragPackage.CalendarItemViewModel);

        UpdateDragOverState(e, dragPackage, hoverTarget);
    }

    private void MonthViewportDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDragPackage(e, out var dragPackage))
        {
            return;
        }

        var hoverTarget = ResolveMonthDropTarget(e.GetPosition(MonthViewport), dragPackage.CalendarItemViewModel);

        UpdateDragOverState(e, dragPackage, hoverTarget);
    }

    private void TimedViewportDrop(object sender, DragEventArgs e)
        => HandleDrop(e, ResolveTimedDropTarget(e.GetPosition(TimedViewport), _activeDragPackage?.CalendarItemViewModel));

    private void TimedAllDayHostDrop(object sender, DragEventArgs e)
        => HandleDrop(e, ResolveTimedAllDayDropTarget(e.GetPosition(TimedAllDayHost), _activeDragPackage?.CalendarItemViewModel));

    private void MonthViewportDrop(object sender, DragEventArgs e)
        => HandleDrop(e, ResolveMonthDropTarget(e.GetPosition(MonthViewport), _activeDragPackage?.CalendarItemViewModel));

    private void CalendarDropTargetDragLeave(object sender, DragEventArgs e)
    {
        _activeDragPackage = null;
        SetHoverTarget(null);
    }

    private bool TryGetDragPackage(DragEventArgs e, [NotNullWhen(true)] out CalendarDragPackage? dragPackage)
    {
        dragPackage = null;

        if (!e.DataView.Properties.ContainsKey(nameof(CalendarDragPackage)) ||
            e.DataView.Properties[nameof(CalendarDragPackage)] is not CalendarDragPackage package ||
            !package.CalendarItemViewModel.CanDragDrop)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            _activeDragPackage = null;
            SetHoverTarget(null);
            return false;
        }

        dragPackage = package;

        return true;
    }

    private void UpdateDragOverState(DragEventArgs e, CalendarDragPackage dragPackage, CalendarDropTargetInfo? hoverTarget)
    {
        _activeDragPackage = dragPackage;
        SetHoverTarget(hoverTarget);

        e.AcceptedOperation = hoverTarget.HasValue
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
    }

    private void HandleDrop(DragEventArgs e, CalendarDropTargetInfo? hoverTarget)
    {
        try
        {
            if (_activeDragPackage?.CalendarItemViewModel is not CalendarItemViewModel droppedItem ||
                !droppedItem.CanDragDrop ||
                hoverTarget is not CalendarDropTargetInfo target)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Move;
            CalendarItemDropped?.Invoke(
                this,
                new CalendarItemDroppedEventArgs(
                    droppedItem,
                    target.TargetStart,
                    target.Kind));
        }
        finally
        {
            _activeDragPackage = null;
            SetHoverTarget(null);
        }
    }

    /// <summary>
    /// The realized-window index and date of the day column under a horizontal viewport coordinate.
    /// </summary>
    private bool TryResolveTimedColumn(double viewportX, out int windowIndex, out DateOnly date)
    {
        windowIndex = -1;
        date = default;

        if (_space is not CalendarRailSpace space || space.Axis != CalendarRailAxis.Horizontal || _unitSize <= 0d || _windowDates.Count == 0)
        {
            return false;
        }

        var viewportWidth = _unitSize * space.VisibleUnitCount;
        var railX = Math.Clamp(viewportX, 0d, Math.Max(0d, viewportWidth - ViewportEdgeEpsilon)) + GetRailOffset();

        windowIndex = Math.Clamp((int)Math.Floor(railX / _unitSize), 0, _windowDates.Count - 1);
        date = _windowDates[windowIndex];

        return true;
    }

    /// <summary>
    /// The realized-window index and date of the month cell under a viewport point.
    /// </summary>
    private bool TryResolveMonthCell(Point viewportPoint, out int windowIndex, out DateOnly date)
    {
        windowIndex = -1;
        date = default;

        if (_space is not CalendarRailSpace space || space.Axis != CalendarRailAxis.Vertical || _unitSize <= 0d || _monthCellWidth <= 0d || _windowDates.Count == 0)
        {
            return false;
        }

        var columnCount = MonthCalendarLayoutCalculator.ColumnCount;
        var rowCount = _windowDates.Count / columnCount;
        var viewportHeight = _unitSize * space.VisibleUnitCount;
        var x = Math.Clamp(viewportPoint.X, 0d, Math.Max(0d, _monthRowWidth - ViewportEdgeEpsilon));
        var railY = Math.Clamp(viewportPoint.Y, 0d, Math.Max(0d, viewportHeight - ViewportEdgeEpsilon)) + GetRailOffset();
        var row = Math.Clamp((int)Math.Floor(railY / _unitSize), 0, rowCount - 1);
        var column = Math.Clamp((int)Math.Floor(x / _monthCellWidth), 0, columnCount - 1);

        windowIndex = (row * columnCount) + column;
        date = _windowDates[windowIndex];

        return true;
    }

    private CalendarDropTargetInfo? ResolveTimedDropTarget(Point position, CalendarItemViewModel? draggedItem)
    {
        if (draggedItem?.IsAllDayEvent == true || !TryResolveTimedColumn(position.X, out var dayIndex, out var date))
        {
            return null;
        }

        var intervalHeight = GetTimedSelectionIntervalHeight();
        var y = Math.Clamp(position.Y, 0d, TimelineHeight);
        var slotIndex = Math.Clamp((int)(y / intervalHeight), 0, (int)((24d * 60d / TimedSelectionIntervalMinutes) - 1));
        var slotStart = TimeSpan.FromMinutes(slotIndex * TimedSelectionIntervalMinutes);

        return new CalendarDropTargetInfo(
            CalendarDropTargetKind.TimedSlot,
            date,
            dayIndex,
            slotIndex,
            date.ToDateTime(TimeOnly.MinValue).Add(slotStart));
    }

    private CalendarDropTargetInfo? ResolveTimedAllDayDropTarget(Point position, CalendarItemViewModel? draggedItem)
    {
        if (draggedItem is { IsAllDayEvent: false } ||
            TimedAllDayHeight <= 0 ||
            !TryResolveTimedColumn(position.X, out var dayIndex, out var date))
        {
            return null;
        }

        return new CalendarDropTargetInfo(
            CalendarDropTargetKind.TimedAllDay,
            date,
            dayIndex,
            -1,
            date.ToDateTime(TimeOnly.MinValue));
    }

    private CalendarDropTargetInfo? ResolveMonthDropTarget(Point position, CalendarItemViewModel? draggedItem)
    {
        if (!TryResolveMonthCell(position, out var cellIndex, out var date))
        {
            return null;
        }

        var targetStart = date.ToDateTime(TimeOnly.MinValue);

        if (draggedItem is { IsAllDayEvent: false })
        {
            targetStart = targetStart.Add(draggedItem.StartDate.TimeOfDay);
        }

        return new CalendarDropTargetInfo(
            CalendarDropTargetKind.MonthCell,
            date,
            cellIndex,
            -1,
            targetStart);
    }

    private void SetHoverTarget(CalendarDropTargetInfo? hoverTarget)
    {
        if (_hoverTarget == hoverTarget)
        {
            return;
        }

        _hoverTarget = hoverTarget;
        InvalidateStructureCanvases();
    }

    /// <summary>
    /// The hover rectangles are located by date, so they stay correct after the window shifts
    /// under a stationary pointer.
    /// </summary>
    private Rect? GetHoveredTimedSlotRect(float dayWidth, float intervalHeight, int intervalCount)
    {
        if (_hoverTarget is not { Kind: CalendarDropTargetKind.TimedSlot } hoverTarget)
        {
            return null;
        }

        var dayIndex = FindWindowDateIndex(hoverTarget.Date);

        if (dayIndex < 0)
        {
            return null;
        }

        var slotIndex = Math.Clamp(hoverTarget.SlotIndex, 0, intervalCount - 1);

        return new Rect(dayIndex * dayWidth, slotIndex * intervalHeight, dayWidth, intervalHeight);
    }

    private Rect? GetHoveredTimedAllDayRect(float dayWidth, float height)
    {
        if (_hoverTarget is not { Kind: CalendarDropTargetKind.TimedAllDay } hoverTarget)
        {
            return null;
        }

        var dayIndex = FindWindowDateIndex(hoverTarget.Date);

        if (dayIndex < 0)
        {
            return null;
        }

        return new Rect(dayIndex * dayWidth, 0, dayWidth, height);
    }

    private Rect? GetHoveredMonthCellRect()
    {
        if (_hoverTarget is not { Kind: CalendarDropTargetKind.MonthCell } hoverTarget)
        {
            return null;
        }

        var cellIndex = FindWindowDateIndex(hoverTarget.Date);

        return cellIndex < 0 ? null : GetMonthCellRect(cellIndex);
    }

    /// <summary>
    /// A month cell's rectangle in the month structure canvas, which spans the realized window.
    /// </summary>
    private Rect GetMonthCellRect(int windowIndex)
    {
        var row = windowIndex / MonthCalendarLayoutCalculator.ColumnCount;
        var column = windowIndex % MonthCalendarLayoutCalculator.ColumnCount;

        return new Rect(column * _monthCellWidth, row * _unitSize, _monthCellWidth, _unitSize);
    }
}
