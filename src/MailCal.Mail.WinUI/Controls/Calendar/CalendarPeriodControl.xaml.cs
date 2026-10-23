using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI;
using Itenso.TimePeriod;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using WinRT;

namespace MailCal.Calendar.Controls;

/// <summary>
/// The calendar surface. It is a viewport over an infinite rail of units (day columns for the
/// timed views, week rows for the month view, see <see cref="CalendarRailSpace"/>). Only the
/// units around the viewport are realized; see CalendarPeriodControl.Rail.cs.
/// </summary>
public sealed partial class CalendarPeriodControl : UserControl, INotifyPropertyChanged
{
    private static readonly TimeSpan SizeRefreshDebounceInterval = TimeSpan.FromMilliseconds(75);
    private const double SizeChangeThreshold = 0.5d;
    private const double TimedHourColumnWidth = 64d;
    private const double TimedHeaderHeight = 44d;
    private const double MonthHeaderHeight = 36d;
    private const double TimedGridIntervalMinutes = 30d;
    private const double TimedSelectionIntervalMinutes = 30d;
    private const double TimedItemRightSpacing = TimedCalendarLayoutCalculator.ItemRightSpacing;

    private INotifyCollectionChanged? _observableItemsSource;
    private bool _areCanvasesReleased;
    private double _timedAllDayHeight;
    private bool _hasPresentedState;
    private bool _fullRefreshPending = true;
    private bool _rangeRefreshPending;
    private bool _refreshScheduled;
    private CalendarDropTargetInfo? _hoverTarget;
    private CalendarDragPackage? _activeDragPackage;
    private CalendarDisplayType _lastDisplayMode = CalendarDisplayType.Month;
    private DayOfWeek _lastFirstDayOfWeek = DayOfWeek.Monday;
    private readonly DispatcherQueueTimer _sizeRefreshTimer;
    private readonly DispatcherQueueTimer _wheelSettleTimer;

    [GeneratedDependencyProperty]
    public partial VisibleDateRange? VisibleRange { get; set; }

    [GeneratedDependencyProperty]
    public partial CalendarSettings? CalendarSettings { get; set; }

    [GeneratedDependencyProperty]
    public partial IReadOnlyList<CalendarItemViewModel>? CalendarItems { get; set; }

    [GeneratedDependencyProperty]
    public partial string? TimedHeaderDateFormat { get; set; }

    [GeneratedDependencyProperty]
    public partial Brush? DefaultHourBackground { get; set; }

    [GeneratedDependencyProperty]
    public partial Brush? WorkHourBackground { get; set; }

    [GeneratedDependencyProperty]
    public partial Brush? SelectedSlotBackground { get; set; }

    [GeneratedDependencyProperty]
    public partial Brush? HoverSlotBackground { get; set; }

    [GeneratedDependencyProperty]
    public partial DateTime? SelectedDateTime { get; set; }

    [GeneratedDependencyProperty]
    public partial DateTime? SelectedEndDateTime { get; set; }

    public CalendarPeriodControl()
    {
        InitializeComponent();

        // These pointer handlers also see events a child already handled: range selection and
        // panning must observe every press, move and release, including those an event control
        // or the hour ScrollViewer handled. XAML attributes cannot ask for handled events, so
        // they are registered here. The wheel is registered in XAML on the elements that raise it.
        AddHandler(PointerPressedEvent, new PointerEventHandler(ControlPointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(ControlPointerMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(ControlPointerReleased), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(ControlPointerCanceled), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(ControlPointerCaptureLost), true);

        _sizeRefreshTimer = DispatcherQueue.CreateTimer();
        _sizeRefreshTimer.Interval = SizeRefreshDebounceInterval;
        _sizeRefreshTimer.IsRepeating = false;
        _sizeRefreshTimer.Tick += SizeRefreshTimerTick;

        _wheelSettleTimer = DispatcherQueue.CreateTimer();
        _wheelSettleTimer.Interval = WheelSettleDelay;
        _wheelSettleTimer.IsRepeating = false;
        _wheelSettleTimer.Tick += WheelSettleTimerTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<CalendarEmptySlotTappedEventArgs>? EmptySlotTapped;
    public event EventHandler<CalendarItemDroppedEventArgs>? CalendarItemDropped;

    /// <summary>
    /// Raised when the rail comes to rest on a period other than the one <see cref="VisibleRange"/> describes.
    /// </summary>
    public event EventHandler<CalendarPeriodEventArgs>? PeriodSettled;

    /// <summary>
    /// Raised while the rail moves, whenever the period nearest to the viewport changes, so the
    /// title can follow the rail before it settles.
    /// </summary>
    public event EventHandler<CalendarPeriodEventArgs>? ViewportPeriodChanged;

    /// <summary>
    /// Raised when the user asks for today with the keyboard (Home).
    /// </summary>
    public event EventHandler? TodayRequested;

    private ObservableCollection<HeaderTextLayout> MonthHeaderTextsCollection { get; } = [];

    public ObservableCollection<HeaderTextLayout> MonthHeaderTexts => MonthHeaderTextsCollection;

    public double TimedAllDayHeight
    {
        get => _timedAllDayHeight;
        private set
        {
            if (_timedAllDayHeight == value)
            {
                return;
            }

            _timedAllDayHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTimedAllDayItems));
        }
    }

    public bool HasTimedAllDayItems => TimedAllDayHeight > 0d;

    public double TimelineHeight => TimedCalendarLayoutCalculator.GetTimelineHeight(GetHourHeight());

    partial void OnVisibleRangeChanged(VisibleDateRange? newValue)
    {
        CancelRangeSelection();
        _rangeRefreshPending = true;
        QueueRefresh();
    }

    partial void OnCalendarSettingsChanged(CalendarSettings? newValue) => RequestRefresh();
    partial void OnTimedHeaderDateFormatChanged(string? newValue) => RequestRefresh();
    partial void OnDefaultHourBackgroundChanged(Brush? newValue) => InvalidateStructureCanvases();
    partial void OnWorkHourBackgroundChanged(Brush? newValue) => InvalidateStructureCanvases();
    partial void OnSelectedSlotBackgroundChanged(Brush? newValue) => InvalidateStructureCanvases();
    partial void OnHoverSlotBackgroundChanged(Brush? newValue) => InvalidateStructureCanvases();
    partial void OnSelectedEndDateTimeChanged(DateTime? newValue) => InvalidateStructureCanvases();
    partial void OnSelectedDateTimeChanged(DateTime? newValue) => InvalidateStructureCanvases();

    partial void OnCalendarItemsChanged(IReadOnlyList<CalendarItemViewModel>? newValue)
    {
        DetachCurrentItemsSource();
        AttachItemsSource(newValue);

        MarkAllUnitsDirty();
        QueueDirtyUnitLayout();
    }

    private void ControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!HasMeaningfulSizeChange(e))
        {
            return;
        }

        var isLiveResize = _hasPresentedState &&
                           e.PreviousSize.Width > 0 &&
                           e.PreviousSize.Height > 0;

        if (isLiveResize)
        {
            _fullRefreshPending = true;
            _sizeRefreshTimer.Stop();
            _sizeRefreshTimer.Start();
            return;
        }

        if (!_fullRefreshPending && !_rangeRefreshPending)
        {
            return;
        }

        QueueRefresh();
    }

    private IEnumerable<CalendarItemViewModel> CurrentItems => CalendarItems ?? [];

    private void AttachItemsSource(IReadOnlyList<CalendarItemViewModel>? itemsSource)
    {
        if (itemsSource is INotifyCollectionChanged observableItemsSource)
        {
            _observableItemsSource = observableItemsSource;
            _observableItemsSource.CollectionChanged += ItemsSourceCollectionChanged;
        }
    }

    private void DetachCurrentItemsSource()
    {
        if (_observableItemsSource is not null)
        {
            _observableItemsSource.CollectionChanged -= ItemsSourceCollectionChanged;
            _observableItemsSource = null;
        }
    }

    private void RequestRefresh()
    {
        _fullRefreshPending = true;
        QueueRefresh();
    }

    private void InvalidateStructureCanvases()
    {
        if (_areCanvasesReleased)
        {
            return;
        }

        TimedAllDayCanvas.Invalidate();
        TimedStructureCanvas.Invalidate();
        MonthStructureCanvas.Invalidate();
    }

    private void InvalidateAllCanvases()
    {
        if (_areCanvasesReleased)
        {
            return;
        }

        TimedHeaderCanvas.Invalidate();
        InvalidateStructureCanvases();
    }

    /// <summary>
    /// Applies pending changes. A full refresh (settings, size, header format, a new display type
    /// or week layout) rebuilds every realized unit; a range change only moves the rail to the
    /// period the view model now shows.
    /// </summary>
    private void Refresh()
    {
        if (!_fullRefreshPending && !_rangeRefreshPending)
        {
            return;
        }

        if (_areCanvasesReleased || !IsLoaded || ActualWidth <= 0 || VisibleRange is not VisibleDateRange visibleRange || CalendarSettings is not CalendarSettings settings)
        {
            return;
        }

        var space = CalendarRailSpace.Create(visibleRange.DisplayType, settings);
        var targetUnit = space.GetPeriodStartUnit(visibleRange.AnchorDate);
        var transition = GetTransitionInfo(visibleRange, settings);
        var isRebuild = _space is null || !_space.HasSameUnits(space);
        var isFullRefresh = _fullRefreshPending || isRebuild;

        _fullRefreshPending = false;
        _rangeRefreshPending = false;
        _space = space;

        if (isRebuild)
        {
            CancelPan();
            StopGlide();
            StopWheelScrolling();
            _requestedUnits.Clear();
            RecycleAllUnits();
            _position = targetUnit;
            _settledUnit = targetUnit;
            _rangeUnit = targetUnit;
        }
        else
        {
            AcceptVisibleRangeUnit(targetUnit);
        }

        if (isFullRefresh)
        {
            ApplyFullLayout(space, settings);
        }
        else
        {
            UpdateRailWindow(forceRelayout: false);
        }

        RunTransition(transition, visibleRange.DisplayType);

        _hasPresentedState = true;
        _lastDisplayMode = visibleRange.DisplayType;
        _lastFirstDayOfWeek = settings.FirstDayOfWeek;
    }

    /// <summary>
    /// Shows the surface for the space's display type, recomputes the unit size and rebuilds
    /// every realized unit.
    /// </summary>
    private void ApplyFullLayout(CalendarRailSpace space, CalendarSettings settings)
    {
        ResolveItemTemplates(settings.EventDisplayMode);

        if (space.Axis == CalendarRailAxis.Vertical)
        {
            TimedRoot.Visibility = Visibility.Collapsed;
            MonthRoot.Visibility = Visibility.Visible;

            var monthBodyHeight = Math.Max(0d, ActualHeight - MonthHeaderHeight);

            _monthRowWidth = ActualWidth;
            _monthCellWidth = ActualWidth / MonthCalendarLayoutCalculator.ColumnCount;
            _unitSize = monthBodyHeight / space.VisibleUnitCount;

            ReplaceCollection(
                MonthHeaderTextsCollection,
                Enumerable.Range(0, MonthCalendarLayoutCalculator.ColumnCount)
                    .Select(index =>
                    {
                        var day = (DayOfWeek)(((int)settings.FirstDayOfWeek + index) % 7);

                        return new HeaderTextLayout(
                            settings.CultureInfo.DateTimeFormat.AbbreviatedDayNames[(int)day],
                            _monthCellWidth);
                    }));
        }
        else
        {
            TimedRoot.Visibility = Visibility.Visible;
            MonthRoot.Visibility = Visibility.Collapsed;
            ResetTimedVisualState();

            var timedSurfaceWidth = GetTimedSurfaceWidth();

            _unitSize = timedSurfaceWidth / space.VisibleUnitCount;

            OnPropertyChanged(nameof(TimelineHeight));
            TimedScrollContentGrid.Width = ActualWidth;
            TimedViewport.Width = timedSurfaceWidth;
            TimedViewport.Height = TimelineHeight;
            TimedAllDayHost.Width = timedSurfaceWidth;

            RenderHourLabels();
        }

        RecycleAllUnits();
        UpdateRailWindow(forceRelayout: true);
        UpdateRailClips();
    }

    private static bool HasMeaningfulSizeChange(SizeChangedEventArgs e)
        => Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > SizeChangeThreshold ||
           Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > SizeChangeThreshold;

    private void QueueRefresh()
    {
        if (_refreshScheduled)
        {
            return;
        }

        _refreshScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshScheduled = false;
            Refresh();
        });
    }

    private void PrepareDisplayMetadata(CalendarItemViewModel item, DateOnly date)
    {
        if (CalendarSettings is null || item is not ICalendarItemViewModel calendarItemViewModel)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            calendarItemViewModel.DisplayingPeriod = new TimeRange(
            date.ToDateTime(TimeOnly.MinValue),
            date.AddDays(1).ToDateTime(TimeOnly.MinValue));
            calendarItemViewModel.CalendarSettings = CalendarSettings;
        });
    }

    private void ControlUnloaded(object sender, RoutedEventArgs e)
    {
        CancelRangeSelection();
        CancelPan();
        StopGlide();
        StopWheelScrolling();
        DetachCurrentItemsSource();
        _sizeRefreshTimer.Stop();
        _sizeRefreshTimer.Tick -= SizeRefreshTimerTick;
        _wheelSettleTimer.Tick -= WheelSettleTimerTick;
        ReleaseCanvases();
        ReleaseRail();
    }

    /// <summary>
    /// A Win2D canvas holds its Draw handler and its parent with references the garbage
    /// collector cannot see through. Left alone, canvas, handler and this control keep each
    /// other alive, and with them the calendar page and every event it shows. The handlers are
    /// removed by hand, the canvases take themselves out of the tree, and in XAML each canvas
    /// has a parent of its own so that nothing else hangs off the reference it keeps. The page
    /// is not cached, so leaving the tree is final.
    /// </summary>
    private void ReleaseCanvases()
    {
        if (_areCanvasesReleased)
        {
            return;
        }

        _areCanvasesReleased = true;

        TimedHeaderCanvas.Draw -= TimedHeaderCanvasDraw;
        TimedAllDayCanvas.Draw -= TimedAllDayCanvasDraw;
        TimedStructureCanvas.Draw -= TimedStructureCanvasDraw;
        MonthStructureCanvas.Draw -= MonthStructureCanvasDraw;

        TimedHeaderCanvas.RemoveFromVisualTree();
        TimedAllDayCanvas.RemoveFromVisualTree();
        TimedStructureCanvas.RemoveFromVisualTree();
        MonthStructureCanvas.RemoveFromVisualTree();
    }

    [DynamicWindowsRuntimeCast(typeof(DependencyObject))]
    private bool IsCalendarItemSource(object originalSource)
    {
        var current = originalSource as DependencyObject;

        while (current is not null && current != this)
        {
            if (current is CalendarItemControl)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void SizeRefreshTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        QueueRefresh();
    }

    private void TimedHeaderCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        MarkCanvasDrawn(ref _timedHeaderCanvasStart, TimedHeaderCanvasHost);

        var borderColor = CreateLineColor();
        var drawingSession = args.DrawingSession;
        drawingSession.Clear(Colors.Transparent);

        if (_windowDates.Count == 0 || _unitSize <= 0)
        {
            return;
        }

        var height = (float)sender.ActualHeight;
        var dayWidth = (float)_unitSize;

        for (var index = 1; index < _windowDates.Count; index++)
        {
            var x = (dayWidth * index) - BoundaryLineInset;
            drawingSession.DrawLine(x, 0, x, height, borderColor);
        }

        drawingSession.DrawLine(0, height - 1, (float)sender.ActualWidth, height - 1, borderColor);
    }

    private void TimedAllDayCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        MarkCanvasDrawn(ref _timedAllDayCanvasStart, TimedAllDayCanvasHost);

        var borderColor = CreateLineColor();
        var hoverFillColor = ToColor(HoverSlotBackground ?? new SolidColorBrush(Colors.Transparent));
        var drawingSession = args.DrawingSession;
        drawingSession.Clear(Colors.Transparent);

        if (_windowDates.Count == 0 || _unitSize <= 0 || TimedAllDayHeight <= 0)
        {
            return;
        }

        var height = (float)sender.ActualHeight;
        var dayWidth = (float)_unitSize;

        var hoveredTimedAllDayRect = GetHoveredTimedAllDayRect(dayWidth, height);
        if (hoveredTimedAllDayRect.HasValue && hoverFillColor.A > 0)
        {
            drawingSession.FillRectangle(hoveredTimedAllDayRect.Value, hoverFillColor);
        }

        for (var index = 1; index < _windowDates.Count; index++)
        {
            var x = (dayWidth * index) - BoundaryLineInset;
            drawingSession.DrawLine(x, 0, x, height, borderColor);
        }

        drawingSession.DrawLine(0, height - 1, (float)sender.ActualWidth, height - 1, borderColor);
    }

    private void TimedStructureCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        MarkCanvasDrawn(ref _timedStructureCanvasStart, TimedStructureCanvasHost);

        var lineColor = CreateLineColor();
        var minorLineColor = CreateMinorLineColor();
        var defaultFillColor = ToColor(DefaultHourBackground ?? new SolidColorBrush(Colors.Transparent));
        var workFillColor = ToColor(WorkHourBackground ?? new SolidColorBrush(Colors.Transparent));
        var selectedFillColor = ToColor(SelectedSlotBackground ?? new SolidColorBrush(Colors.Transparent));
        var hoverFillColor = ToColor(HoverSlotBackground ?? new SolidColorBrush(Colors.Transparent));
        var drawingSession = args.DrawingSession;
        drawingSession.Clear(Colors.Transparent);

        if (_windowDates.Count == 0 || _unitSize <= 0)
        {
            return;
        }

        var hourHeight = GetHourHeight();
        var timelineHeight = TimedCalendarLayoutCalculator.GetTimelineHeight(hourHeight);
        var scaleY = (float)(sender.ActualHeight / timelineHeight);
        var dayWidth = (float)_unitSize;
        var isWorkingHoursEnabled = CalendarSettings?.IsWorkingHoursEnabled == true;
        var workDayStartHour = CalendarSettings?.WorkingHourStart.TotalHours ?? 9d;
        var workDayEndHour = CalendarSettings?.WorkingHourEnd.TotalHours ?? 17d;
        var intervalHeight = (float)(GetTimedGridIntervalHeight() * scaleY);
        var intervalCount = (int)(24d * 60d / TimedGridIntervalMinutes);

        for (var dayIndex = 0; dayIndex < _windowDates.Count; dayIndex++)
        {
            var x = dayIndex * dayWidth;
            var isWorkingDay = isWorkingHoursEnabled &&
                               CalendarSettings?.WorkingDays.Contains(_windowDates[dayIndex].DayOfWeek) == true;

            for (var intervalIndex = 0; intervalIndex < intervalCount; intervalIndex++)
            {
                var intervalStartHour = (intervalIndex * TimedGridIntervalMinutes) / 60d;
                var y = intervalIndex * intervalHeight;
                var fillColor = isWorkingDay && intervalStartHour >= workDayStartHour && intervalStartHour < workDayEndHour
                    ? workFillColor
                    : defaultFillColor;
                drawingSession.FillRectangle(x, y, dayWidth, intervalHeight, fillColor);
            }
        }

        var hoveredTimedSlotRect = GetHoveredTimedSlotRect(dayWidth, intervalHeight, intervalCount);
        if (hoveredTimedSlotRect.HasValue && hoverFillColor.A > 0)
        {
            drawingSession.FillRectangle(hoveredTimedSlotRect.Value, hoverFillColor);
        }

        if (CurrentSelection is CalendarSelectionRange selection && selectedFillColor.A > 0)
        {
            for (var dayIndex = 0; dayIndex < _windowDates.Count; dayIndex++)
            {
                var date = _windowDates[dayIndex];
                if (selection.IntersectDay(date) is not CalendarSelectionRange segment)
                    continue;

                var dayStart = date.ToDateTime(TimeOnly.MinValue);
                var y = (segment.Start - dayStart).TotalMinutes / TimedGridIntervalMinutes * intervalHeight;
                var height = (segment.End - segment.Start).TotalMinutes / TimedGridIntervalMinutes * intervalHeight;
                drawingSession.FillRectangle(new Rect(dayIndex * dayWidth, y, dayWidth, height), selectedFillColor);
            }
        }

        for (var intervalIndex = 0; intervalIndex <= intervalCount; intervalIndex++)
        {
            var y = intervalIndex * intervalHeight;
            var color = intervalIndex % 2 == 0 ? lineColor : minorLineColor;
            drawingSession.DrawLine(0, y, (float)sender.ActualWidth, y, color);
        }

        for (var index = 0; index <= _windowDates.Count; index++)
        {
            var x = dayWidth * index;
            drawingSession.DrawLine(x, 0, x, (float)sender.ActualHeight, lineColor);
        }
    }

    private void MonthStructureCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        MarkCanvasDrawn(ref _monthStructureCanvasStart, MonthStructureCanvasHost);

        var lineColor = CreateLineColor();
        var todayColor = Color.FromArgb(26, 0, 120, 215);
        var selectedColor = ToColor(SelectedSlotBackground ?? new SolidColorBrush(Colors.Transparent));
        var hoverColor = ToColor(HoverSlotBackground ?? new SolidColorBrush(Colors.Transparent));

        var drawingSession = args.DrawingSession;
        drawingSession.Clear(Colors.Transparent);

        if (_monthCellWidth <= 0 || _unitSize <= 0 || _windowDates.Count == 0)
        {
            return;
        }

        var cellWidth = (float)_monthCellWidth;
        var cellHeight = (float)_unitSize;
        var rowCount = _windowDates.Count / MonthCalendarLayoutCalculator.ColumnCount;
        var today = DateOnly.FromDateTime(DateTime.Now.Date);

        var todayIndex = FindWindowDateIndex(today);
        if (todayIndex >= 0)
        {
            drawingSession.FillRectangle(GetMonthCellRect(todayIndex), todayColor);
        }

        var hoveredMonthCellRect = GetHoveredMonthCellRect();
        if (hoveredMonthCellRect.HasValue && hoverColor.A > 0)
        {
            drawingSession.FillRectangle(hoveredMonthCellRect.Value, hoverColor);
        }

        if (CurrentSelection is CalendarSelectionRange selection && selectedColor.A > 0)
        {
            for (var index = 0; index < _windowDates.Count; index++)
            {
                if (selection.IntersectDay(_windowDates[index]) != null)
                    drawingSession.FillRectangle(GetMonthCellRect(index), selectedColor);
            }
        }

        for (var row = 0; row <= rowCount; row++)
        {
            var y = row * cellHeight;
            drawingSession.DrawLine(0, y, (float)sender.ActualWidth, y, lineColor);
        }

        for (var column = 0; column <= MonthCalendarLayoutCalculator.ColumnCount; column++)
        {
            var x = column * cellWidth;
            drawingSession.DrawLine(x, 0, x, (float)sender.ActualHeight, lineColor);
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void RenderHourLabels()
    {
        HourLabelsCanvas.Children.Clear();
        HourLabelsCanvas.Height = TimelineHeight;

        var hourHeight = GetHourHeight();
        var labelWidth = Math.Max(0d, TimedHourColumnWidth - 10d);

        for (var hour = 0; hour <= 24; hour++)
        {
            var textBlock = new TextBlock
            {
                Width = labelWidth,
                Text = GetTimedHourLabelText(hour),
                TextAlignment = TextAlignment.Right,
                Opacity = 0.72
            };

            var y = hour == 24
                ? Math.Max(0d, TimelineHeight - 20d)
                : Math.Max(0d, (hour * hourHeight) - 10d);

            Canvas.SetLeft(textBlock, 0d);
            Canvas.SetTop(textBlock, y);
            HourLabelsCanvas.Children.Add(textBlock);
        }
    }

    private int FindWindowDateIndex(DateOnly date)
    {
        for (var index = 0; index < _windowDates.Count; index++)
        {
            if (_windowDates[index] == date)
            {
                return index;
            }
        }

        return -1;
    }

    private double GetTimedSurfaceWidth() => Math.Max(0d, ActualWidth - TimedHourColumnWidth);

    private string GetTimedHeaderText(DateOnly date)
    {
        if (!string.IsNullOrWhiteSpace(TimedHeaderDateFormat) && CalendarSettings is not null)
        {
            try
            {
                return date.ToDateTime(TimeOnly.MinValue).ToString(TimedHeaderDateFormat, CalendarSettings.CultureInfo);
            }
            catch (FormatException)
            {
            }
        }

        return CalendarSettings?.GetTimedDayHeaderText(date) ?? date.ToDateTime(TimeOnly.MinValue).ToString("ddd dd");
    }

    private string GetTimedHourLabelText(int hour)
        => CalendarSettings?.GetTimedHourLabelText(hour) ?? $"{hour:00}:00";

    private CalendarTransitionKind GetTransitionInfo(VisibleDateRange visibleRange, CalendarSettings settings)
    {
        if (!_hasPresentedState)
        {
            return CalendarTransitionKind.None;
        }

        if (_lastDisplayMode != visibleRange.DisplayType)
        {
            return CalendarTransitionKind.ModeChange;
        }

        if (_lastFirstDayOfWeek != settings.FirstDayOfWeek)
        {
            return CalendarTransitionKind.Refresh;
        }

        return CalendarTransitionKind.None;
    }

    private void RunTransition(CalendarTransitionKind transition, CalendarDisplayType displayType)
    {
        if (transition == CalendarTransitionKind.None)
        {
            return;
        }

        if (displayType != CalendarDisplayType.Month)
        {
            RunTimedTransition(transition);
            return;
        }

        var target = (UIElement)MonthRoot;
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;

        visual.CenterPoint = new Vector3((float)(target.RenderSize.Width * 0.5), (float)(target.RenderSize.Height * 0.5), 0f);
        visual.StopAnimation(nameof(visual.Offset));
        visual.StopAnimation(nameof(visual.Opacity));
        visual.StopAnimation(nameof(visual.Scale));

        switch (transition)
        {
            case CalendarTransitionKind.ModeChange:
                StartModeTransition(compositor, visual);
                break;
            case CalendarTransitionKind.Refresh:
                StartRefreshTransition(compositor, visual);
                break;
        }
    }

    private void RunTimedTransition(CalendarTransitionKind transition)
    {
        var contentVisual = ElementCompositionPreview.GetElementVisual(TimedScrollViewer);
        var compositor = contentVisual.Compositor;

        PrepareAnimatedVisual(contentVisual, TimedScrollViewer);

        switch (transition)
        {
            case CalendarTransitionKind.ModeChange:
                StartTimedModeTransition(compositor);
                break;
            case CalendarTransitionKind.Refresh:
                StartTimedRefreshTransition(compositor);
                break;
        }
    }

    private void ResetTimedVisualState()
    {
        ResetAnimatedElement(TimedScrollViewer);

        // TODO: This is buggy.
        // ResetAnimatedElement(TimedAllDayHost);
    }

    private static void StartModeTransition(Compositor compositor, Visual visual)
    {
        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(0f, new Vector3(0f, 18f, 0f));
        offsetAnimation.InsertKeyFrame(1f, Vector3.Zero);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(260);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0f, 0f);
        opacityAnimation.InsertKeyFrame(1f, 1f);
        opacityAnimation.Duration = offsetAnimation.Duration;

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.InsertKeyFrame(0f, new Vector3(0.985f, 0.985f, 1f));
        scaleAnimation.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        scaleAnimation.Duration = offsetAnimation.Duration;

        visual.StartAnimation(nameof(visual.Offset), offsetAnimation);
        visual.StartAnimation(nameof(visual.Opacity), opacityAnimation);
        visual.StartAnimation(nameof(visual.Scale), scaleAnimation);
    }

    private void StartTimedModeTransition(Compositor compositor)
    {
        StartTimedElementTransition(compositor, TimedScrollViewer, 0f, 18f, 0f, TimeSpan.FromMilliseconds(240), 0f, 0f, animateScale: false);
        if (HasTimedAllDayItems)
        {
            // TODO: This is buggy.
            // StartTimedElementTransition(compositor, TimedAllDayHost, 0f, 18f, 0f, TimeSpan.FromMilliseconds(240), 0f, 0f, animateScale: false);
        }
    }

    private static void StartRefreshTransition(Compositor compositor, Visual visual)
    {
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0f, 0.82f);
        opacityAnimation.InsertKeyFrame(1f, 1f);
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(160);

        visual.StartAnimation(nameof(visual.Opacity), opacityAnimation);
    }

    private void StartTimedRefreshTransition(Compositor compositor)
    {
        StartOpacityTransition(compositor, ElementCompositionPreview.GetElementVisual(TimedScrollViewer), 0.8f, TimeSpan.FromMilliseconds(160));
        if (HasTimedAllDayItems)
        {
            // TODO: This is buggy.
            // StartOpacityTransition(compositor, ElementCompositionPreview.GetElementVisual(TimedAllDayHost), 0.8f, TimeSpan.FromMilliseconds(160));
        }
    }

    private static void PrepareAnimatedVisual(Visual visual, UIElement target)
    {
        visual.CenterPoint = new Vector3((float)(target.RenderSize.Width * 0.5), (float)(target.RenderSize.Height * 0.5), 0f);
        visual.StopAnimation(nameof(visual.Offset));
        visual.StopAnimation(nameof(visual.Opacity));
        visual.StopAnimation(nameof(visual.Scale));
    }

    [DynamicWindowsRuntimeCast(typeof(InsetClip))]
    private static void ResetAnimatedElement(UIElement target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        PrepareAnimatedVisual(visual, target);

        visual.Offset = Vector3.Zero;
        visual.Opacity = 1f;
        visual.Scale = new Vector3(1f, 1f, 1f);

        if (visual.Clip is InsetClip clip)
        {
            clip.StopAnimation(nameof(clip.LeftInset));
            clip.StopAnimation(nameof(clip.RightInset));
            clip.LeftInset = 0f;
            clip.RightInset = 0f;
        }
    }

    [DynamicWindowsRuntimeCast(typeof(InsetClip))]
    private static void StartTimedElementTransition(Compositor compositor, UIElement target, float offsetX, float offsetY, float startingOpacity, TimeSpan duration, float leftInset, float rightInset, bool animateScale = true)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        PrepareAnimatedVisual(visual, target);

        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var fadeEasing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        var clip = visual.Clip as InsetClip ?? compositor.CreateInsetClip();

        clip.LeftInset = leftInset;
        clip.RightInset = rightInset;
        visual.Clip = clip;

        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(0f, new Vector3(offsetX, offsetY, 0f));
        offsetAnimation.InsertKeyFrame(1f, Vector3.Zero, easing);
        offsetAnimation.Duration = duration;

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0f, startingOpacity);
        opacityAnimation.InsertKeyFrame(1f, 1f, fadeEasing);
        opacityAnimation.Duration = duration;

        var leftInsetAnimation = compositor.CreateScalarKeyFrameAnimation();
        leftInsetAnimation.InsertKeyFrame(1f, 0f, easing);
        leftInsetAnimation.Duration = duration;

        var rightInsetAnimation = compositor.CreateScalarKeyFrameAnimation();
        rightInsetAnimation.InsertKeyFrame(1f, 0f, easing);
        rightInsetAnimation.Duration = duration;

        visual.StartAnimation(nameof(visual.Offset), offsetAnimation);
        visual.StartAnimation(nameof(visual.Opacity), opacityAnimation);

        if (animateScale)
        {
            var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.InsertKeyFrame(0f, new Vector3(0.996f, 0.996f, 1f));
            scaleAnimation.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f), easing);
            scaleAnimation.Duration = duration;
            visual.StartAnimation(nameof(visual.Scale), scaleAnimation);
        }
        else
        {
            visual.Scale = new Vector3(1f, 1f, 1f);
        }

        clip.StartAnimation(nameof(clip.LeftInset), leftInsetAnimation);
        clip.StartAnimation(nameof(clip.RightInset), rightInsetAnimation);
    }

    private static void StartOpacityTransition(Compositor compositor, Visual visual, float startingOpacity, TimeSpan duration)
    {
        visual.StopAnimation(nameof(visual.Opacity));

        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0f, startingOpacity);
        opacityAnimation.InsertKeyFrame(1f, 1f, easing);
        opacityAnimation.Duration = duration;

        visual.StartAnimation(nameof(visual.Opacity), opacityAnimation);
    }

    private static Color CreateLineColor()
    {
        var strokeColor = GetStrokeColor();

        return Color.FromArgb((byte)Math.Max(40, strokeColor.A / 2), strokeColor.R, strokeColor.G, strokeColor.B);
    }

    private static Color CreateMinorLineColor()
    {
        var strokeColor = GetStrokeColor();

        return Color.FromArgb((byte)Math.Max(20, strokeColor.A / 4), strokeColor.R, strokeColor.G, strokeColor.B);
    }

    [DynamicWindowsRuntimeCast(typeof(SolidColorBrush))]
    private static Color ToColor(Brush brush)
    {
        return brush is SolidColorBrush solidColorBrush
            ? solidColorBrush.Color
            : Colors.Transparent;
    }

    private static double GetTimedGridIntervalHeight(double hourHeight) => hourHeight * (TimedGridIntervalMinutes / 60d);

    private double GetTimedGridIntervalHeight() => GetTimedGridIntervalHeight(GetHourHeight());

    private double GetTimedSelectionIntervalHeight() => GetHourHeight() * (TimedSelectionIntervalMinutes / 60d);

    private double GetHourHeight() => CalendarSettings?.HourHeight ?? 60d;

    [DynamicWindowsRuntimeCast(typeof(SolidColorBrush))]
    private static Color GetStrokeColor()
    {
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var resource) &&
            resource is SolidColorBrush solidBrush)
        {
            return solidBrush.Color;
        }

        return Color.FromArgb(96, 210, 210, 210);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly record struct CalendarDropTargetInfo(
        CalendarDropTargetKind Kind,
        DateOnly Date,
        int DayIndex,
        int SlotIndex,
        DateTime TargetStart);

    private enum CalendarTransitionKind
    {
        None,
        ModeChange,
        Refresh
    }
}
