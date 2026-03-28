using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI;
using Itenso.TimePeriod;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.Foundation;
using Windows.UI;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public sealed partial class CalendarPeriodControl : UserControl, INotifyPropertyChanged
{
    private static readonly TimeSpan SizeRefreshDebounceInterval = TimeSpan.FromMilliseconds(75);
    private const double SizeChangeThreshold = 0.5d;
    private const double TimedHourColumnWidth = 64d;
    private const double TimedGridIntervalMinutes = 30d;
    private const double TimedSelectionIntervalMinutes = 30d;
    private const double TimedItemRightSpacing = 10d;
    private VisibleDateRange _currentRange = new(
        CalendarDisplayType.Month,
        DateOnly.FromDateTime(DateTime.Today),
        DateOnly.FromDateTime(DateTime.Today),
        DateOnly.FromDateTime(DateTime.Today),
        DateOnly.FromDateTime(DateTime.Today),
        1,
        true,
        true,
        [DateOnly.FromDateTime(DateTime.Today)]);

    private TimedCalendarLayoutResult _timedLayout = new([], 0, []);
    private MonthCalendarLayoutResult _monthLayout = new(0, 0, [], []);
    private INotifyCollectionChanged? _observableItemsSource;
    private double _timedDayWidth;
    private double _timedAllDayHeight;
    private double _monthCellWidth;
    private double _monthCellHeight;
    private bool _hasPresentedState;
    private bool _refreshPending = true;
    private bool _refreshScheduled;
    private CalendarDisplayType _lastDisplayMode = CalendarDisplayType.Month;
    private DateOnly _lastDisplayDate = DateOnly.FromDateTime(DateTime.Today);
    private DayOfWeek _lastFirstDayOfWeek = DayOfWeek.Monday;
    private readonly DispatcherQueueTimer _sizeRefreshTimer;

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
    public partial DateTime? SelectedDateTime { get; set; }

    public CalendarPeriodControl()
    {
        InitializeComponent();

        _sizeRefreshTimer = DispatcherQueue.CreateTimer();
        _sizeRefreshTimer.Interval = SizeRefreshDebounceInterval;
        _sizeRefreshTimer.IsRepeating = false;
        _sizeRefreshTimer.Tick += SizeRefreshTimerTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<CalendarEmptySlotTappedEventArgs>? EmptySlotTapped;

    private ObservableCollection<HeaderTextLayout> TimedHeaderTextsCollection { get; } = [];
    private ObservableCollection<HeaderTextLayout> MonthHeaderTextsCollection { get; } = [];
    private ObservableCollection<TimedItemLayout> TimedItemsCollection { get; } = [];
    private ObservableCollection<TimedItemLayout> TimedAllDayItemsCollection { get; } = [];
    private ObservableCollection<MonthCellLabelLayout> MonthCellLabelsCollection { get; } = [];
    private ObservableCollection<MonthItemLayout> MonthItemsCollection { get; } = [];

    public IEnumerable<HeaderTextLayout> TimedHeaderTexts => TimedHeaderTextsCollection;
    public IEnumerable<HeaderTextLayout> MonthHeaderTexts => MonthHeaderTextsCollection;
    public IEnumerable<TimedItemLayout> TimedItems => TimedItemsCollection;
    public IEnumerable<TimedItemLayout> TimedAllDayItems => TimedAllDayItemsCollection;
    public IEnumerable<MonthCellLabelLayout> MonthCellLabels => MonthCellLabelsCollection;
    public IEnumerable<MonthItemLayout> MonthItems => MonthItemsCollection;

    public double TimedDayWidth
    {
        get => _timedDayWidth;
        private set
        {
            if (_timedDayWidth == value)
            {
                return;
            }

            _timedDayWidth = value;
            OnPropertyChanged();
        }
    }

    public double MonthCellWidth
    {
        get => _monthCellWidth;
        private set
        {
            if (_monthCellWidth == value)
            {
                return;
            }

            _monthCellWidth = value;
            OnPropertyChanged();
        }
    }

    public double MonthCellHeight
    {
        get => _monthCellHeight;
        private set
        {
            if (_monthCellHeight == value)
            {
                return;
            }

            _monthCellHeight = value;
            OnPropertyChanged();
        }
    }

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

    partial void OnVisibleRangeChanged(VisibleDateRange? newValue) => RequestRefresh();
    partial void OnCalendarSettingsChanged(CalendarSettings? newValue) => RequestRefresh();
    partial void OnTimedHeaderDateFormatChanged(string? newValue) => RequestRefresh();
    partial void OnSelectedSlotBackgroundChanged(Brush? newValue) => InvalidateStructureCanvases();
    partial void OnSelectedDateTimeChanged(DateTime? newValue) => InvalidateStructureCanvases();

    partial void OnCalendarItemsChanged(IReadOnlyList<CalendarItemViewModel>? newValue)
    {
        DetachCurrentItemsSource();
        AttachItemsSource(newValue);
        RequestRefresh();
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
            _refreshPending = true;
            _sizeRefreshTimer.Stop();
            _sizeRefreshTimer.Start();
            return;
        }

        if (!_refreshPending)
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

    private void DetachItemsSource(IReadOnlyList<CalendarItemViewModel>? itemsSource)
    {
        var observableItemsSource = itemsSource as INotifyCollectionChanged;

        if (observableItemsSource is not null)
        {
            observableItemsSource.CollectionChanged -= ItemsSourceCollectionChanged;
        }

        if (ReferenceEquals(_observableItemsSource, observableItemsSource))
        {
            _observableItemsSource = null;
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

    private void ItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestRefresh();

    private void RequestRefresh()
    {
        _refreshPending = true;
        QueueRefresh();
    }

    private void InvalidateStructureCanvases()
    {
        TimedStructureCanvas.Invalidate();
        MonthStructureCanvas.Invalidate();
    }

    private void Refresh()
    {
        if (!_refreshPending || !IsLoaded || ActualWidth <= 0 || VisibleRange is null || CalendarSettings is null)
        {
            return;
        }

        var transition = GetTransitionInfo();
        _currentRange = CreateLayoutRange(VisibleRange, CalendarSettings);

        if (VisibleRange.DisplayType == CalendarDisplayType.Month)
        {
            RefreshMonthView();
        }
        else
        {
            RefreshTimedView();
        }

        RunTransition(transition);
        _hasPresentedState = true;
        _refreshPending = false;
        _lastDisplayMode = VisibleRange.DisplayType;
        _lastDisplayDate = VisibleRange.AnchorDate;
        _lastFirstDayOfWeek = CalendarSettings.FirstDayOfWeek;

        Debug.WriteLine($"Refreshed control.");
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

    private void SizeRefreshTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        QueueRefresh();
    }

    private void RefreshTimedView()
    {
        TimedRoot.Visibility = Visibility.Visible;
        MonthRoot.Visibility = Visibility.Collapsed;
        ResetTimedVisualState();

        var timedSurfaceWidth = GetTimedSurfaceWidth();

        TimedDayWidth = _currentRange.Dates.Count == 0 ? 0d : timedSurfaceWidth / _currentRange.Dates.Count;
        TimedAllDayHeight = TimedCalendarLayoutCalculator.GetAllDayHeight(
            TimedCalendarLayoutCalculator.GetAllDayLaneCount(_currentRange.Dates, CurrentItems));
        TimedScrollContentGrid.Width = ActualWidth;
        TimedViewport.Width = timedSurfaceWidth;
        TimedViewport.Height = TimelineHeight;
        TimedAllDayHost.Width = timedSurfaceWidth;
        TimedAllDayItemsCanvas.Width = timedSurfaceWidth;
        TimedAllDayItemsCanvas.Height = TimedAllDayHeight;

        _timedLayout = TimedCalendarLayoutCalculator.Calculate(_currentRange, CurrentItems, timedSurfaceWidth, GetHourHeight());

        ReplaceCollection(
            TimedHeaderTextsCollection,
            _timedLayout.VisibleDates.Select(date =>
                new HeaderTextLayout(
                    GetTimedHeaderText(date),
                    TimedDayWidth)));

        var eventTemplate = (DataTemplate)Resources["CalendarEventTemplate"];
        ReplaceCollection(TimedAllDayItemsCollection, TimedCalendarLayoutCalculator.CalculateAllDayItems(_currentRange, CurrentItems, timedSurfaceWidth).Select(item =>
        {
            PrepareDisplayMetadata(item.Item, item.Date);
            item.Template = eventTemplate;
            return item;
        }));
        ReplaceCollection(TimedItemsCollection, _timedLayout.Items.Select(item =>
        {
            PrepareDisplayMetadata(item.Item, item.Date);
            item.Template = eventTemplate;
            return item;
        }));
        RenderHourLabels();
        RenderTimedAllDayItems();
        RenderTimedItems();

        TimedAllDayCanvas.Invalidate();
        TimedHeaderCanvas.Invalidate();
        TimedStructureCanvas.Invalidate();
    }

    private void RefreshMonthView()
    {
        TimedRoot.Visibility = Visibility.Collapsed;
        MonthRoot.Visibility = Visibility.Visible;

        var availableMonthHeight = Math.Max(0d, ActualHeight - MonthHeadersItemsControl.ActualHeight);
        _monthLayout = MonthCalendarLayoutCalculator.Calculate(_currentRange, CurrentItems, ActualWidth, availableMonthHeight);

        MonthCellWidth = _monthLayout.CellWidth;
        MonthCellHeight = _monthLayout.CellHeight;

        MonthViewport.Width = ActualWidth;
        MonthViewport.Height = availableMonthHeight;

        ReplaceCollection(
            MonthHeaderTextsCollection,
            Enumerable.Range(0, MonthCalendarLayoutCalculator.ColumnCount)
                .Select(index =>
                {
                    var day = (DayOfWeek)(((int)CalendarSettings!.FirstDayOfWeek + index) % 7);
                    return new HeaderTextLayout(
                        CalendarSettings.CultureInfo.DateTimeFormat.AbbreviatedDayNames[(int)day],
                        MonthCellWidth);
                }));

        ReplaceCollection(
            MonthCellLabelsCollection,
            _monthLayout.Cells.Select(cell =>
                new MonthCellLabelLayout(
                    cell.Date.Day.ToString(CalendarSettings!.CultureInfo),
                    cell.Date.Month == VisibleRange!.AnchorDate.Month && cell.Date.Year == VisibleRange.AnchorDate.Year ? 1d : 0.55d,
                    new LayoutRect(cell.Bounds.X + 4, cell.Bounds.Y + 2, cell.Bounds.Width, cell.Bounds.Height))));

        var monthEventTemplate = (DataTemplate)Resources["MonthEventTemplate"];
        ReplaceCollection(MonthItemsCollection, _monthLayout.Items.Select(item =>
        {
            PrepareDisplayMetadata(item.Item, item.Date);
            item.Template = monthEventTemplate;
            return item;
        }));
        RenderMonthCellLabels();
        RenderMonthItems();

        MonthStructureCanvas.Invalidate();
    }

    private void PrepareDisplayMetadata(CalendarItemViewModel item, DateOnly date)
    {
        if (CalendarSettings is null || item is not ICalendarItemViewModel calendarItemViewModel)
        {
            return;
        }

        calendarItemViewModel.DisplayingPeriod = new TimeRange(
            date.ToDateTime(TimeOnly.MinValue),
            date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        calendarItemViewModel.CalendarSettings = CalendarSettings;
    }

    private static VisibleDateRange CreateLayoutRange(VisibleDateRange visibleRange, CalendarSettings calendarSettings)
    {
        if (visibleRange.DisplayType != CalendarDisplayType.Month)
        {
            return visibleRange;
        }

        var start = AlignToWeekStart(visibleRange.StartDate, calendarSettings.FirstDayOfWeek);
        var end = AlignToWeekEnd(visibleRange.EndDate, calendarSettings.FirstDayOfWeek);
        var totalDays = end.DayNumber - start.DayNumber + 1;

        if (totalDays <= 35)
        {
            end = start.AddDays(34);
        }
        else if (totalDays < 42)
        {
            end = start.AddDays(41);
        }

        var dates = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1)
                              .Select(offset => start.AddDays(offset))
                              .ToArray();

        return new VisibleDateRange(
            visibleRange.DisplayType,
            visibleRange.AnchorDate,
            start,
            end,
            visibleRange.PrimaryDate,
            dates.Length,
            dates.Contains(DateOnly.FromDateTime(DateTime.Today)),
            start.Year == end.Year && start.Month == end.Month,
            dates);
    }

    private static DateOnly AlignToWeekStart(DateOnly date, DayOfWeek firstDayOfWeek)
    {
        var offset = ((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return date.AddDays(-offset);
    }

    private static DateOnly AlignToWeekEnd(DateOnly date, DayOfWeek firstDayOfWeek)
    {
        var lastDayOfWeek = (DayOfWeek)(((int)firstDayOfWeek + 6) % 7);
        var offset = ((int)lastDayOfWeek - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset);
    }

    private void TimedHeaderCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        using var borderPaint = CreateLinePaint();
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var timedSurfaceWidth = GetTimedSurfaceWidth();

        if (_timedLayout.VisibleDates.Count == 0 || timedSurfaceWidth <= 0)
        {
            return;
        }

        var scaleX = (float)(e.Info.Width / timedSurfaceWidth);
        var height = e.Info.Height;
        var dayWidth = (float)(_timedLayout.DayWidth * scaleX);

        for (var index = 1; index < _timedLayout.VisibleDates.Count; index++)
        {
            var x = dayWidth * index;
            canvas.DrawLine(x, 0, x, height, borderPaint);
        }

        canvas.DrawLine(0, height - 1, e.Info.Width, height - 1, borderPaint);
    }

    private void TimedAllDayCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        using var borderPaint = CreateLinePaint();
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var timedSurfaceWidth = GetTimedSurfaceWidth();

        if (_timedLayout.VisibleDates.Count == 0 || timedSurfaceWidth <= 0 || TimedAllDayHeight <= 0)
        {
            return;
        }

        var scaleX = (float)(e.Info.Width / timedSurfaceWidth);
        var height = e.Info.Height;
        var dayWidth = (float)(_timedLayout.DayWidth * scaleX);

        for (var index = 1; index < _timedLayout.VisibleDates.Count; index++)
        {
            var x = dayWidth * index;
            canvas.DrawLine(x, 0, x, height, borderPaint);
        }

        canvas.DrawLine(0, height - 1, e.Info.Width, height - 1, borderPaint);
    }

    private void TimedStructureCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        using var linePaint = CreateLinePaint();
        using var minorLinePaint = CreateMinorLinePaint();
        using var defaultFillPaint = CreateFillPaint(DefaultHourBackground ?? new SolidColorBrush(Colors.Transparent));
        using var workFillPaint = CreateFillPaint(WorkHourBackground ?? new SolidColorBrush(Colors.Transparent));
        using var selectedFillPaint = CreateFillPaint(SelectedSlotBackground ?? new SolidColorBrush(Colors.Transparent));
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var timedSurfaceWidth = GetTimedSurfaceWidth();

        if (_timedLayout.VisibleDates.Count == 0 || timedSurfaceWidth <= 0)
        {
            return;
        }

        var hourHeight = GetHourHeight();
        var timelineHeight = TimedCalendarLayoutCalculator.GetTimelineHeight(hourHeight);
        var scaleX = (float)(e.Info.Width / timedSurfaceWidth);
        var scaleY = (float)(e.Info.Height / timelineHeight);
        var dayWidth = (float)(_timedLayout.DayWidth * scaleX);
        var isWorkingHoursEnabled = CalendarSettings?.IsWorkingHoursEnabled == true;
        var workDayStartHour = CalendarSettings?.WorkingHourStart.TotalHours ?? 9d;
        var workDayEndHour = CalendarSettings?.WorkingHourEnd.TotalHours ?? 17d;
        var intervalHeight = (float)(GetTimedGridIntervalHeight() * scaleY);
        var intervalCount = (int)(24d * 60d / TimedGridIntervalMinutes);

        for (var dayIndex = 0; dayIndex < _timedLayout.VisibleDates.Count; dayIndex++)
        {
            var x = dayIndex * dayWidth;
            var isWorkingDay = isWorkingHoursEnabled &&
                               CalendarSettings?.WorkingDays.Contains(_timedLayout.VisibleDates[dayIndex].DayOfWeek) == true;

            for (var intervalIndex = 0; intervalIndex < intervalCount; intervalIndex++)
            {
                var intervalStartHour = (intervalIndex * TimedGridIntervalMinutes) / 60d;
                var y = intervalIndex * intervalHeight;
                var fillPaint = isWorkingDay && intervalStartHour >= workDayStartHour && intervalStartHour < workDayEndHour
                    ? workFillPaint
                    : defaultFillPaint;
                canvas.DrawRect(x, y, dayWidth, intervalHeight, fillPaint);
            }
        }

        var selectedTimedSlotRect = GetSelectedTimedSlotRect(dayWidth, intervalHeight, intervalCount);
        if (selectedTimedSlotRect.HasValue && selectedFillPaint.Color.Alpha > 0)
        {
            canvas.DrawRect(selectedTimedSlotRect.Value, selectedFillPaint);
        }

        for (var intervalIndex = 0; intervalIndex <= intervalCount; intervalIndex++)
        {
            var y = intervalIndex * intervalHeight;
            var paint = intervalIndex % 2 == 0 ? linePaint : minorLinePaint;
            canvas.DrawLine(0, y, e.Info.Width, y, paint);
        }

        for (var index = 0; index <= _timedLayout.VisibleDates.Count; index++)
        {
            var x = dayWidth * index;
            canvas.DrawLine(x, 0, x, e.Info.Height, linePaint);
        }
    }

    private void MonthStructureCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        using var linePaint = CreateLinePaint();
        using var todayPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0, 120, 215, 26),
            IsAntialias = true
        };
        using var selectedPaint = CreateFillPaint(SelectedSlotBackground ?? new SolidColorBrush(Colors.Transparent));

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (_monthLayout.CellWidth <= 0 || _monthLayout.CellHeight <= 0 || MonthViewport.ActualWidth <= 0 || MonthViewport.ActualHeight <= 0)
        {
            return;
        }

        var cellWidth = (float)(e.Info.Width / MonthCalendarLayoutCalculator.ColumnCount);
        var cellHeight = (float)(e.Info.Height / MonthCalendarLayoutCalculator.RowCount);
        var today = DateOnly.FromDateTime(DateTime.Now.Date);

        foreach (var cell in _monthLayout.Cells)
        {
            if (cell.Date != today)
            {
                continue;
            }

            canvas.DrawRect((float)cell.Bounds.X, (float)cell.Bounds.Y, (float)cell.Bounds.Width, (float)cell.Bounds.Height, todayPaint);
        }

        var selectedMonthCellRect = GetSelectedMonthCellRect();
        if (selectedMonthCellRect.HasValue && selectedPaint.Color.Alpha > 0)
        {
            canvas.DrawRect(selectedMonthCellRect.Value, selectedPaint);
        }

        for (var row = 0; row <= MonthCalendarLayoutCalculator.RowCount; row++)
        {
            var y = row * cellHeight;
            canvas.DrawLine(0, y, e.Info.Width, y, linePaint);
        }

        for (var column = 0; column <= MonthCalendarLayoutCalculator.ColumnCount; column++)
        {
            var x = column * cellWidth;
            canvas.DrawLine(x, 0, x, e.Info.Height, linePaint);
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

    private void RenderTimedItems()
    {
        TimedItemsCanvas.Children.Clear();

        foreach (var item in TimedItemsCollection)
        {
            var presenter = new ContentPresenter
            {
                Width = Math.Max(0d, item.Bounds.Width - TimedItemRightSpacing),
                Height = item.Bounds.Height,
                Content = item.Item,
                ContentTemplate = item.Template
            };

            Canvas.SetLeft(presenter, item.Bounds.X);
            Canvas.SetTop(presenter, item.Bounds.Y);
            TimedItemsCanvas.Children.Add(presenter);
        }
    }

    private void RenderTimedAllDayItems()
    {
        TimedAllDayItemsCanvas.Children.Clear();

        foreach (var item in TimedAllDayItemsCollection)
        {
            var presenter = new ContentPresenter
            {
                Width = item.Bounds.Width,
                Height = item.Bounds.Height,
                Content = item.Item,
                ContentTemplate = item.Template
            };

            Canvas.SetLeft(presenter, item.Bounds.X);
            Canvas.SetTop(presenter, item.Bounds.Y);
            TimedAllDayItemsCanvas.Children.Add(presenter);
        }
    }

    private void RenderMonthCellLabels()
    {
        MonthCellLabelsCanvas.Children.Clear();

        foreach (var label in MonthCellLabelsCollection)
        {
            var textBlock = new TextBlock
            {
                Width = label.Bounds.Width,
                Height = label.Bounds.Height,
                Opacity = label.LabelOpacity,
                Text = label.DayText
            };

            Canvas.SetLeft(textBlock, label.Bounds.X);
            Canvas.SetTop(textBlock, label.Bounds.Y);
            MonthCellLabelsCanvas.Children.Add(textBlock);
        }
    }

    private void RenderMonthItems()
    {
        MonthItemsCanvas.Children.Clear();

        foreach (var item in MonthItemsCollection)
        {
            var presenter = new ContentPresenter
            {
                Width = item.Bounds.Width,
                Height = item.Bounds.Height,
                Content = item.Item,
                ContentTemplate = item.Template
            };

            Canvas.SetLeft(presenter, item.Bounds.X);
            Canvas.SetTop(presenter, item.Bounds.Y);
            MonthItemsCanvas.Children.Add(presenter);
        }
    }

    private void TimedInteractionLayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_timedLayout.VisibleDates.Count == 0 || _timedLayout.DayWidth <= 0)
        {
            return;
        }

        var position = e.GetPosition(TimedViewport);
        var dayIndex = Math.Clamp((int)(position.X / _timedLayout.DayWidth), 0, _timedLayout.VisibleDates.Count - 1);
        var intervalHeight = GetTimedSelectionIntervalHeight();
        var slotIndex = Math.Clamp((int)(position.Y / intervalHeight), 0, (int)((24d * 60d / TimedSelectionIntervalMinutes) - 1));
        var slotStart = TimeSpan.FromMinutes(slotIndex * TimedSelectionIntervalMinutes);
        var clickedDate = _timedLayout.VisibleDates[dayIndex].ToDateTime(TimeOnly.MinValue).Add(slotStart);
        var anchorPoint = TimedViewport.TransformToVisual(Root).TransformPoint(position);

        EmptySlotTapped?.Invoke(
            this,
            new CalendarEmptySlotTappedEventArgs(
                clickedDate,
                anchorPoint,
                new Size(_timedLayout.DayWidth, intervalHeight)));
    }

    private void MonthInteractionLayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_monthLayout.Cells.Count == 0 || _monthLayout.CellWidth <= 0 || _monthLayout.CellHeight <= 0)
        {
            return;
        }

        var position = e.GetPosition(MonthViewport);
        var column = Math.Clamp((int)(position.X / _monthLayout.CellWidth), 0, MonthCalendarLayoutCalculator.ColumnCount - 1);
        var row = Math.Clamp((int)(position.Y / _monthLayout.CellHeight), 0, MonthCalendarLayoutCalculator.RowCount - 1);
        var cellIndex = Math.Clamp((row * MonthCalendarLayoutCalculator.ColumnCount) + column, 0, _monthLayout.Cells.Count - 1);
        var cell = _monthLayout.Cells[cellIndex];
        var anchorPoint = MonthViewport.TransformToVisual(Root).TransformPoint(position);

        EmptySlotTapped?.Invoke(
            this,
            new CalendarEmptySlotTappedEventArgs(
                cell.Date.ToDateTime(TimeOnly.MinValue),
                anchorPoint,
                new Size(cell.Bounds.Width, cell.Bounds.Height)));
    }

    private SKRect? GetSelectedTimedSlotRect(float dayWidth, float intervalHeight, int intervalCount)
    {
        if (SelectedDateTime is not DateTime selectedDateTime || _timedLayout.VisibleDates.Count == 0)
        {
            return null;
        }

        var dayIndex = FindVisibleDateIndex(DateOnly.FromDateTime(selectedDateTime));
        if (dayIndex < 0)
        {
            return null;
        }

        var slotIndex = (int)Math.Floor(selectedDateTime.TimeOfDay.TotalMinutes / TimedSelectionIntervalMinutes);
        slotIndex = Math.Clamp(slotIndex, 0, intervalCount - 1);

        var x = dayIndex * dayWidth;
        var y = slotIndex * intervalHeight;
        return new SKRect(x, y, x + dayWidth, y + intervalHeight);
    }

    private SKRect? GetSelectedMonthCellRect()
    {
        if (SelectedDateTime is not DateTime selectedDateTime)
        {
            return null;
        }

        var selectedDate = DateOnly.FromDateTime(selectedDateTime);
        foreach (var cell in _monthLayout.Cells)
        {
            if (cell.Date != selectedDate)
            {
                continue;
            }

            return new SKRect(
                (float)cell.Bounds.X,
                (float)cell.Bounds.Y,
                (float)(cell.Bounds.X + cell.Bounds.Width),
                (float)(cell.Bounds.Y + cell.Bounds.Height));
        }

        return null;
    }

    private int FindVisibleDateIndex(DateOnly date)
    {
        for (var index = 0; index < _timedLayout.VisibleDates.Count; index++)
        {
            if (_timedLayout.VisibleDates[index] == date)
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

    private CalendarTransitionInfo GetTransitionInfo()
    {
        if (!_hasPresentedState || VisibleRange is null || CalendarSettings is null)
        {
            return new CalendarTransitionInfo(CalendarTransitionKind.None, 0);
        }

        if (_lastDisplayMode != VisibleRange.DisplayType)
        {
            return new CalendarTransitionInfo(CalendarTransitionKind.ModeChange, 0);
        }

        if (_lastDisplayDate != VisibleRange.AnchorDate)
        {
            return new CalendarTransitionInfo(
                CalendarTransitionKind.Navigation,
                VisibleRange.AnchorDate.CompareTo(_lastDisplayDate));
        }

        if (_lastFirstDayOfWeek != CalendarSettings.FirstDayOfWeek)
        {
            return new CalendarTransitionInfo(CalendarTransitionKind.Refresh, 0);
        }

        return new CalendarTransitionInfo(CalendarTransitionKind.None, 0);
    }

    private void RunTransition(CalendarTransitionInfo transition)
    {
        if (transition.Kind == CalendarTransitionKind.None || VisibleRange is null)
        {
            return;
        }

        if (VisibleRange.DisplayType != CalendarDisplayType.Month)
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

        switch (transition.Kind)
        {
            case CalendarTransitionKind.Navigation:
                StartNavigationTransition(compositor, visual, transition.Direction, target.RenderSize.Width);
                break;
            case CalendarTransitionKind.ModeChange:
                StartModeTransition(compositor, visual);
                break;
            case CalendarTransitionKind.Refresh:
                StartRefreshTransition(compositor, visual);
                break;
        }
    }

    private void RunTimedTransition(CalendarTransitionInfo transition)
    {
        var contentVisual = ElementCompositionPreview.GetElementVisual(TimedScrollViewer);
        var compositor = contentVisual.Compositor;

        PrepareAnimatedVisual(contentVisual, TimedScrollViewer);

        switch (transition.Kind)
        {
            case CalendarTransitionKind.Navigation:
                StartTimedNavigationTransition(compositor, transition.Direction);
                break;
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
        ResetAnimatedElement(TimedAllDayHost);
    }

    private static void StartNavigationTransition(Compositor compositor, Visual visual, int direction, double width)
    {
        var travel = (float)Math.Max(48d, Math.Min(160d, width * 0.08d));
        var startX = direction >= 0 ? travel : -travel;

        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(0f, new Vector3(startX, 0f, 0f));
        offsetAnimation.InsertKeyFrame(1f, Vector3.Zero);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(220);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0f, 0.72f);
        opacityAnimation.InsertKeyFrame(1f, 1f);
        opacityAnimation.Duration = offsetAnimation.Duration;

        visual.StartAnimation(nameof(visual.Offset), offsetAnimation);
        visual.StartAnimation(nameof(visual.Opacity), opacityAnimation);
    }

    private void StartTimedNavigationTransition(Compositor compositor, int direction)
    {
        var width = Math.Max(TimedRoot.RenderSize.Width, ActualWidth);
        var travel = (float)Math.Max(56d, Math.Min(184d, width * 0.09d));
        var signedTravel = direction >= 0 ? travel : -travel;
        var clipInset = (float)Math.Max(18d, Math.Min(64d, width * 0.05d));

        StartTimedElementTransition(compositor, TimedScrollViewer, signedTravel, 0f, 0.68f, TimeSpan.FromMilliseconds(240), direction >= 0 ? 0f : clipInset, direction >= 0 ? clipInset : 0f, animateScale: false);
        if (HasTimedAllDayItems)
        {
            StartTimedElementTransition(compositor, TimedAllDayHost, signedTravel, 0f, 0.68f, TimeSpan.FromMilliseconds(240), direction >= 0 ? 0f : clipInset, direction >= 0 ? clipInset : 0f, animateScale: false);
        }
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
            StartTimedElementTransition(compositor, TimedAllDayHost, 0f, 18f, 0f, TimeSpan.FromMilliseconds(240), 0f, 0f, animateScale: false);
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
            StartOpacityTransition(compositor, ElementCompositionPreview.GetElementVisual(TimedAllDayHost), 0.8f, TimeSpan.FromMilliseconds(160));
        }
    }

    private static void PrepareAnimatedVisual(Visual visual, UIElement target)
    {
        visual.CenterPoint = new Vector3((float)(target.RenderSize.Width * 0.5), (float)(target.RenderSize.Height * 0.5), 0f);
        visual.StopAnimation(nameof(visual.Offset));
        visual.StopAnimation(nameof(visual.Opacity));
        visual.StopAnimation(nameof(visual.Scale));
    }

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

    private static SKPaint CreateLinePaint()
    {
        var strokeColor = GetStrokeColor();

        return new SKPaint
        {
            Color = new SKColor(strokeColor.R, strokeColor.G, strokeColor.B, (byte)Math.Max(40, strokeColor.A / 2)),
            IsAntialias = false,
            StrokeWidth = 1
        };
    }

    private static SKPaint CreateMinorLinePaint()
    {
        var strokeColor = GetStrokeColor();

        return new SKPaint
        {
            Color = new SKColor(strokeColor.R, strokeColor.G, strokeColor.B, (byte)Math.Max(20, strokeColor.A / 4)),
            IsAntialias = false,
            StrokeWidth = 1
        };
    }

    private static SKPaint CreateFillPaint(Brush brush)
    {
        return new SKPaint
        {
            Color = ToSkColor(brush),
            Style = SKPaintStyle.Fill,
            IsAntialias = false
        };
    }

    private static SKColor ToSkColor(Brush brush)
    {
        return brush is SolidColorBrush solidColorBrush
            ? new SKColor(solidColorBrush.Color.R, solidColorBrush.Color.G, solidColorBrush.Color.B, solidColorBrush.Color.A)
            : SKColors.Transparent;
    }

    private static double GetTimedGridIntervalHeight(double hourHeight) => hourHeight * (TimedGridIntervalMinutes / 60d);

    private double GetTimedGridIntervalHeight() => GetTimedGridIntervalHeight(GetHourHeight());

    private double GetTimedSelectionIntervalHeight() => GetHourHeight() * (TimedSelectionIntervalMinutes / 60d);

    private double GetHourHeight() => CalendarSettings?.HourHeight ?? 60d;

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

    private readonly record struct CalendarTransitionInfo(CalendarTransitionKind Kind, int Direction);

    private enum CalendarTransitionKind
    {
        None,
        Navigation,
        ModeChange,
        Refresh
    }
}
