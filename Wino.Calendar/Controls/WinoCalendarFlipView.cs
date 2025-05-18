using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public partial class WinoCalendarFlipView : CustomCalendarFlipView
{
    public static readonly DependencyProperty IsIdleProperty = DependencyProperty.Register(nameof(IsIdle), typeof(bool), typeof(WinoCalendarFlipView), new PropertyMetadata(true));
    public static readonly DependencyProperty ActiveCanvasProperty = DependencyProperty.Register(nameof(ActiveCanvas), typeof(WinoDayTimelineCanvas), typeof(WinoCalendarFlipView), new PropertyMetadata(null));
    public static readonly DependencyProperty ActiveVerticalScrollViewerProperty = DependencyProperty.Register(nameof(ActiveVerticalScrollViewer), typeof(ScrollViewer), typeof(WinoCalendarFlipView), new PropertyMetadata(null));

    /// <summary>
    /// Gets or sets the active canvas that is currently displayed in the flip view.
    /// Each day-range of flip view item has a canvas that displays the day timeline.
    /// </summary>
    public WinoDayTimelineCanvas ActiveCanvas
    {
        get { return (WinoDayTimelineCanvas)GetValue(ActiveCanvasProperty); }
        set { SetValue(ActiveCanvasProperty, value); }
    }

    /// <summary>
    /// Gets or sets the scroll viewer that is currently active in the flip view.
    /// It's the vertical scroll that scrolls the timeline only, not the header part that belongs
    /// to parent FlipView control.
    /// </summary>
    public ScrollViewer ActiveVerticalScrollViewer
    {
        get { return (ScrollViewer)GetValue(ActiveVerticalScrollViewerProperty); }
        set { SetValue(ActiveVerticalScrollViewerProperty, value); }
    }

    public bool IsIdle
    {
        get { return (bool)GetValue(IsIdleProperty); }
        set { SetValue(IsIdleProperty, value); }
    }

    public WinoCalendarFlipView()
    {
        RegisterPropertyChangedCallback(SelectedIndexProperty, new DependencyPropertyChangedCallback(OnSelectedIndexUpdated));
        RegisterPropertyChangedCallback(ItemsSourceProperty, new DependencyPropertyChangedCallback(OnItemsSourceChanged));
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyProperty e)
    {
        if (d is WinoCalendarFlipView flipView)
        {
            flipView.RegisterItemsSourceChange();
        }
    }

    private static void OnSelectedIndexUpdated(DependencyObject d, DependencyProperty e)
    {
        if (d is WinoCalendarFlipView flipView)
        {
            flipView.UpdateActiveCanvas();
            flipView.UpdateActiveScrollViewer();
        }
    }

    private void RegisterItemsSourceChange()
    {
        if (GetItemsSource() is INotifyCollectionChanged notifyCollectionChanged)
        {
            notifyCollectionChanged.CollectionChanged += ItemsSourceUpdated;
        }
    }

    private void ItemsSourceUpdated(object sender, NotifyCollectionChangedEventArgs e)
    {
        IsIdle = e.Action == NotifyCollectionChangedAction.Reset || e.Action == NotifyCollectionChangedAction.Replace;
    }

    private async Task<FlipViewItem> GetCurrentFlipViewItem()
    {
        // TODO: Refactor this mechanism by listening to PrepareContainerForItemOverride and Loaded events together.
        while (ContainerFromIndex(SelectedIndex) == null)
        {
            await Task.Delay(100);
        }

        return ContainerFromIndex(SelectedIndex) as FlipViewItem;


    }

    private void UpdateActiveScrollViewer()
    {
        if (SelectedIndex < 0)
            ActiveVerticalScrollViewer = null;
        else
        {
            GetCurrentFlipViewItem().ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var flipViewItem = task.Result;

                    _ = Dispatcher.TryRunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        ActiveVerticalScrollViewer = flipViewItem.FindDescendant<ScrollViewer>();
                    });
                }
            });
        }
    }

    public void UpdateActiveCanvas()
    {
        if (SelectedIndex < 0)
            ActiveCanvas = null;
        else
        {
            GetCurrentFlipViewItem().ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var flipViewItem = task.Result;

                    _ = Dispatcher.TryRunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        ActiveCanvas = flipViewItem.FindDescendant<WinoDayTimelineCanvas>();
                    });
                }
            });
        }
    }

    /// <summary>
    /// Navigates to the specified date in the calendar.
    /// </summary>
    /// <param name="dateTime">Date to navigate.</param>
    public async void NavigateToDay(DateTime dateTime)
    {
        await Task.Yield();

        await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
        {
            // Find the day range that contains the date.
            var dayRange = GetItemsSource()?.FirstOrDefault(a => a.CalendarDays.Any(b => b.RepresentingDate.Date == dateTime.Date));

            if (dayRange != null)
            {
                var navigationItemIndex = GetItemsSource().IndexOf(dayRange);

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
        });
    }

    private ObservableRangeCollection<DayRangeRenderModel> GetItemsSource()
        => ItemsSource as ObservableRangeCollection<DayRangeRenderModel>;
}
