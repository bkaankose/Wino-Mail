using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.Args;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Helpers;

namespace Wino.Calendar.Controls;

public partial class WinoCalendarControl : Control
{
    private const string PART_WinoFlipView = nameof(PART_WinoFlipView);
    private const string PART_IdleGrid = nameof(PART_IdleGrid);

    public event EventHandler<TimelineCellSelectedArgs> TimelineCellSelected;
    public event EventHandler<TimelineCellUnselectedArgs> TimelineCellUnselected;

    public event EventHandler ScrollPositionChanging;

    #region Dependency Properties

    /// <summary>
    /// Gets or sets the collection of day ranges to render.
    /// Each day range usually represents a week, but it may support other ranges.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial ObservableCollection<DayRangeRenderModel>? DayRanges { get; set; }

    [GeneratedDependencyProperty(DefaultValue = -1)]
    public partial int SelectedFlipViewIndex { get; set; }

    [GeneratedDependencyProperty]
    public partial DayRangeRenderModel? SelectedFlipViewDayRange { get; set; }

    [GeneratedDependencyProperty]
    public partial WinoDayTimelineCanvas? ActiveCanvas { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsFlipIdle { get; set; }

    [GeneratedDependencyProperty]
    public partial ScrollViewer? ActiveScrollViewer { get; set; }

    [GeneratedDependencyProperty]
    public partial ItemsPanelTemplate? VerticalItemsPanelTemplate { get; set; }

    [GeneratedDependencyProperty]
    public partial ItemsPanelTemplate? HorizontalItemsPanelTemplate { get; set; }

    [GeneratedDependencyProperty(DefaultValue = CalendarOrientation.Horizontal)]
    public partial CalendarOrientation Orientation { get; set; }

    /// <summary>
    /// Gets or sets the day-week-month-year display type.
    /// Orientation is not determined by this property, but Orientation property.
    /// This property is used to determine the template to use for the calendar.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = CalendarDisplayType.Day)]
    public partial CalendarDisplayType DisplayType { get; set; }

    #endregion

    private WinoCalendarFlipView InternalFlipView;
    private Grid IdleGrid;

    private ScrollViewer? _previousScrollViewer;
    private WinoDayTimelineCanvas? _previousCanvas;

    public WinoCalendarControl()
    {
        DefaultStyleKey = typeof(WinoCalendarControl);
        SizeChanged += CalendarSizeChanged;
    }

    partial void OnVerticalItemsPanelTemplateChanged(ItemsPanelTemplate? newValue)
        => ManageCalendarOrientation();

    partial void OnHorizontalItemsPanelTemplateChanged(ItemsPanelTemplate? newValue)
        => ManageCalendarOrientation();

    partial void OnOrientationChanged(CalendarOrientation newValue)
        => ManageCalendarOrientation();

    partial void OnIsFlipIdleChanged(bool newValue)
        => UpdateIdleState();

    partial void OnActiveScrollViewerPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        var newValue = e.NewValue as ScrollViewer;
        if (_previousScrollViewer != null)
        {
            DeregisterScrollChanges(_previousScrollViewer);
        }

        if (newValue != null)
        {
            RegisterScrollChanges(newValue);
        }

        _previousScrollViewer = newValue;
        ManageHighlightedDateRange();
    }

    partial void OnActiveCanvasPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        var newValue = e.NewValue as WinoDayTimelineCanvas;
        if (_previousCanvas != null)
        {
            DeregisterCanvas(_previousCanvas);
        }

        if (newValue != null)
        {
            RegisterCanvas(newValue);
        }

        _previousCanvas = newValue;
        ManageHighlightedDateRange();
    }

    private void ManageCalendarOrientation()
    {
        if (InternalFlipView == null || HorizontalItemsPanelTemplate == null || VerticalItemsPanelTemplate == null) return;

        InternalFlipView.ItemsPanel = Orientation == CalendarOrientation.Horizontal ? HorizontalItemsPanelTemplate : VerticalItemsPanelTemplate;
    }

    private void ManageHighlightedDateRange()
        => SelectedFlipViewDayRange = InternalFlipView.SelectedItem as DayRangeRenderModel;

    private void DeregisterCanvas(WinoDayTimelineCanvas canvas)
    {
        if (canvas == null) return;

        canvas.SelectedDateTime = null;
        canvas.TimelineCellSelected -= ActiveTimelineCellSelected;
        canvas.TimelineCellUnselected -= ActiveTimelineCellUnselected;
    }

    private void RegisterCanvas(WinoDayTimelineCanvas canvas)
    {
        if (canvas == null) return;

        canvas.SelectedDateTime = null;
        canvas.TimelineCellSelected += ActiveTimelineCellSelected;
        canvas.TimelineCellUnselected += ActiveTimelineCellUnselected;
    }

    private void RegisterScrollChanges(ScrollViewer scrollViewer)
    {
        if (scrollViewer == null) return;

        scrollViewer.ViewChanging += ScrollViewChanging;
    }

    private void DeregisterScrollChanges(ScrollViewer scrollViewer)
    {
        if (scrollViewer == null) return;

        scrollViewer.ViewChanging -= ScrollViewChanging;
    }

    private void ScrollViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        => ScrollPositionChanging?.Invoke(this, EventArgs.Empty);

    private void CalendarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActiveCanvas == null) return;

        ActiveCanvas.SelectedDateTime = null;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        InternalFlipView = GetTemplateChild(PART_WinoFlipView) as WinoCalendarFlipView;
        IdleGrid = GetTemplateChild(PART_IdleGrid) as Grid;

        UpdateIdleState();
        ManageCalendarOrientation();
    }

    private void UpdateIdleState()
    {
        InternalFlipView.Opacity = IsFlipIdle ? 0 : 1;
        IdleGrid.Visibility = IsFlipIdle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActiveTimelineCellUnselected(object sender, TimelineCellUnselectedArgs e)
        => TimelineCellUnselected?.Invoke(this, e);

    private void ActiveTimelineCellSelected(object sender, TimelineCellSelectedArgs e)
        => TimelineCellSelected?.Invoke(this, e);

    public void NavigateToDay(DateTime dateTime) => InternalFlipView.NavigateToDay(dateTime);

    public async void NavigateToHour(TimeSpan timeSpan)
    {
        if (ActiveScrollViewer == null) return;

        // Total height of the FlipViewItem is the same as vertical ScrollViewer to position day headers.

        await Task.Yield();
        await DispatcherQueue.EnqueueAsync(() =>
        {
            if (ActiveScrollViewer == null) return;

            double hourHeght = 60;
            double totalHeight = ActiveScrollViewer.ScrollableHeight;
            double scrollPosition = timeSpan.TotalHours * hourHeght;

            ActiveScrollViewer.ChangeView(null, scrollPosition, null, disableAnimation: false);
        });
    }
    public void ResetTimelineSelection()
    {
        if (ActiveCanvas == null) return;

        ActiveCanvas.SelectedDateTime = null;
    }

    public void GoNextRange()
    {
        if (InternalFlipView == null) return;

        InternalFlipView.GoNextFlip();
    }

    public void GoPreviousRange()
    {
        if (InternalFlipView == null) return;

        InternalFlipView.GoPreviousFlip();
    }

    public void UnselectActiveTimelineCell()
    {
        if (ActiveCanvas == null) return;

        ActiveCanvas.SelectedDateTime = null;
    }

    public CalendarItemControl GetCalendarItemControl(CalendarItemViewModel calendarItemViewModel)
    {
        return this.FindDescendants<CalendarItemControl>().FirstOrDefault(a => a.CalendarItem == calendarItemViewModel);
    }
}
