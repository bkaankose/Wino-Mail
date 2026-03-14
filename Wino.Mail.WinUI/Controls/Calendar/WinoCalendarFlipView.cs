using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public partial class WinoCalendarFlipView : CustomCalendarFlipView, IDisposable
{
    public static readonly DependencyProperty IsIdleProperty = DependencyProperty.Register(nameof(IsIdle), typeof(bool), typeof(WinoCalendarFlipView), new PropertyMetadata(true));
    public static readonly DependencyProperty ActiveCanvasProperty = DependencyProperty.Register(nameof(ActiveCanvas), typeof(WinoDayTimelineCanvas), typeof(WinoCalendarFlipView), new PropertyMetadata(null));
    public static readonly DependencyProperty ActiveVerticalScrollViewerProperty = DependencyProperty.Register(nameof(ActiveVerticalScrollViewer), typeof(ScrollViewer), typeof(WinoCalendarFlipView), new PropertyMetadata(null));

    /// <summary>
    /// Gets or sets the active canvas that is currently displayed in the flip view.
    /// Each day-range of flip view item has a canvas that displays the day timeline.
    /// </summary>
    public WinoDayTimelineCanvas? ActiveCanvas
    {
        get { return (WinoDayTimelineCanvas?)GetValue(ActiveCanvasProperty); }
        set { SetValue(ActiveCanvasProperty, value); }
    }

    /// <summary>
    /// Gets or sets the scroll viewer that is currently active in the flip view.
    /// It's the vertical scroll that scrolls the timeline only, not the header part that belongs
    /// to parent FlipView control.
    /// </summary>
    public ScrollViewer? ActiveVerticalScrollViewer
    {
        get { return (ScrollViewer?)GetValue(ActiveVerticalScrollViewerProperty); }
        set { SetValue(ActiveVerticalScrollViewerProperty, value); }
    }

    public bool IsIdle
    {
        get { return (bool)GetValue(IsIdleProperty); }
        set { SetValue(IsIdleProperty, value); }
    }

    internal bool IsProgrammaticNavigationInProgress { get; private set; }

    internal int? PendingTargetIndex { get; private set; }

    internal event EventHandler<ProgrammaticNavigationCompletedEventArgs>? ProgrammaticNavigationCompleted;

    private INotifyCollectionChanged? _trackedItemsSource;
    private readonly long _itemsSourceCallbackToken;
    private FrameworkElement? _pendingActiveElementContainer;
    private bool _isActiveElementResolutionPending;

    public WinoCalendarFlipView()
    {
        _itemsSourceCallbackToken = RegisterPropertyChangedCallback(ItemsSourceProperty, new DependencyPropertyChangedCallback(OnItemsSourceChanged));
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyProperty e)
    {
        if (d is WinoCalendarFlipView flipView)
        {
            flipView.RegisterItemsSourceChange();
        }
    }

    private void RegisterItemsSourceChange()
    {
        if (_trackedItemsSource != null)
        {
            _trackedItemsSource.CollectionChanged -= ItemsSourceUpdated;
        }

        _trackedItemsSource = GetItemsSource();

        if (_trackedItemsSource != null)
        {
            _trackedItemsSource.CollectionChanged += ItemsSourceUpdated;
        }

        UpdateIdleState();
    }

    protected override void OnSelectedItemChanged(object? oldValue, object? newValue)
    {
        base.OnSelectedItemChanged(oldValue, newValue);

        UpdateActiveElements();
    }

    protected override void OnContainerPrepared(DependencyObject element, object item)
    {
        base.OnContainerPrepared(element, item);

        // Check if this is the currently selected item's container
        var index = IndexFromContainer(element);
        if (index >= 0 && index == SelectedIndex)
        {
            // Container for selected item is now ready, update active elements
            UpdateActiveElements();
        }
    }

    private void ItemsSourceUpdated(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateIdleState();
    }

    private void UpdateIdleState()
    {
        var itemsSource = GetItemsSource();
        IsIdle = itemsSource == null || itemsSource.Count == 0;
    }

    private void UpdateActiveElements()
    {
        var itemsSource = GetItemsSource();

        if (SelectedIndex < 0)
        {
            CancelPendingActiveElementResolution();

            if (itemsSource == null || itemsSource.Count == 0)
            {
                ActiveCanvas = null;
                ActiveVerticalScrollViewer = null;
            }

            return;
        }

        if (TryResolveActiveElements())
        {
            CancelPendingActiveElementResolution();
        }
        else
        {
            ScheduleActiveElementResolution();
        }
    }

    private bool TryResolveActiveElements()
    {
        if (SelectedIndex < 0)
        {
            return false;
        }

        if (ContainerFromIndex(SelectedIndex) is not FlipViewItem container)
        {
            return false;
        }

        var activeCanvas = container.FindDescendant<WinoDayTimelineCanvas>();
        var activeVerticalScrollViewer = container.FindDescendant<ScrollViewer>();

        if (activeCanvas == null && activeVerticalScrollViewer == null)
        {
            return false;
        }

        ActiveCanvas = activeCanvas;
        ActiveVerticalScrollViewer = activeVerticalScrollViewer;

        return true;
    }

    private void ScheduleActiveElementResolution()
    {
        if (ContainerFromIndex(SelectedIndex) is FrameworkElement container &&
            !ReferenceEquals(_pendingActiveElementContainer, container))
        {
            if (_pendingActiveElementContainer != null)
            {
                _pendingActiveElementContainer.Loaded -= PendingActiveElementContainerLoaded;
            }

            _pendingActiveElementContainer = container;
            _pendingActiveElementContainer.Loaded += PendingActiveElementContainerLoaded;
        }

        if (_isActiveElementResolutionPending)
        {
            return;
        }

        _isActiveElementResolutionPending = true;
        LayoutUpdated += FlipViewLayoutUpdated;

        DispatcherQueue.TryEnqueue(RetryActiveElementResolution);
    }

    private void RetryActiveElementResolution()
    {
        if (TryResolveActiveElements())
        {
            CancelPendingActiveElementResolution();
        }
    }

    private void PendingActiveElementContainerLoaded(object sender, RoutedEventArgs e)
        => RetryActiveElementResolution();

    private void FlipViewLayoutUpdated(object? sender, object e)
        => RetryActiveElementResolution();

    private void CancelPendingActiveElementResolution()
    {
        if (_pendingActiveElementContainer != null)
        {
            _pendingActiveElementContainer.Loaded -= PendingActiveElementContainerLoaded;
            _pendingActiveElementContainer = null;
        }

        if (_isActiveElementResolutionPending)
        {
            LayoutUpdated -= FlipViewLayoutUpdated;
            _isActiveElementResolutionPending = false;
        }
    }

    /// <summary>
    /// Navigates to the specified date in the calendar.
    /// </summary>
    /// <param name="dateTime">Date to navigate.</param>
    public async void NavigateToDay(DateTime dateTime)
    {
        await Task.Yield();

        await DispatcherQueue.EnqueueAsync(() =>
        {
            // Find the day range that contains the date.
            var dayRanges = GetItemsSource();
            var dayRange = dayRanges?.FirstOrDefault(a => a.CalendarDays.Any(b => b.RepresentingDate.Date == dateTime.Date));

            if (dayRange != null && dayRanges != null)
            {
                var navigationItemIndex = dayRanges.IndexOf(dayRange);
                var hasNavigationWork = navigationItemIndex != SelectedIndex;

                IsProgrammaticNavigationInProgress = hasNavigationWork;
                PendingTargetIndex = navigationItemIndex;

                if (!hasNavigationWork)
                {
                    PendingTargetIndex = null;
                    return;
                }

                try
                {
                    if (Math.Abs(navigationItemIndex - SelectedIndex) > 4)
                    {
                        // Difference between dates are high.
                        // No need to animate this much, just go without animating.

                        SelectedIndex = navigationItemIndex;
                    }
                    else
                    {
                        // Until we reach the day in the flip, simulate next-prev button clicks.
                        // This will make sure the FlipView animations are triggered.
                        // Setting SelectedIndex directly doesn't trigger the animations.

                        while (SelectedIndex != navigationItemIndex)
                        {
                            if (SelectedIndex > navigationItemIndex)
                            {
                                GoPreviousFlip();
                            }
                            else
                            {
                                GoNextFlip();
                            }
                        }
                    }
                }
                finally
                {
                    if (SelectedIndex == PendingTargetIndex)
                    {
                        ProgrammaticNavigationCompleted?.Invoke(this, new ProgrammaticNavigationCompletedEventArgs(SelectedItem as DayRangeRenderModel ?? dayRange));
                    }

                    IsProgrammaticNavigationInProgress = false;
                    PendingTargetIndex = null;
                }
            }
        });
    }

    private ObservableRangeCollection<DayRangeRenderModel>? GetItemsSource()
        => ItemsSource as ObservableRangeCollection<DayRangeRenderModel>;

    public void Dispose()
    {
        CancelPendingActiveElementResolution();

        if (_trackedItemsSource != null)
        {
            _trackedItemsSource.CollectionChanged -= ItemsSourceUpdated;
            _trackedItemsSource = null;
        }

        UnregisterPropertyChangedCallback(ItemsSourceProperty, _itemsSourceCallbackToken);
        ProgrammaticNavigationCompleted = null;
    }
}

internal sealed class ProgrammaticNavigationCompletedEventArgs : EventArgs
{
    public ProgrammaticNavigationCompletedEventArgs(DayRangeRenderModel dayRange)
    {
        DayRange = dayRange;
    }

    public DayRangeRenderModel DayRange { get; }
}
