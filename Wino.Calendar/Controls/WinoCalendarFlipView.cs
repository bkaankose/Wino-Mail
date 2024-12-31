using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarFlipView : CustomCalendarFlipView
    {
        public static readonly DependencyProperty IsIdleProperty = DependencyProperty.Register(nameof(IsIdle), typeof(bool), typeof(WinoCalendarFlipView), new PropertyMetadata(true));
        public static readonly DependencyProperty ActiveCanvasProperty = DependencyProperty.Register(nameof(ActiveCanvas), typeof(WinoDayTimelineCanvas), typeof(WinoCalendarFlipView), new PropertyMetadata(null));

        public WinoDayTimelineCanvas ActiveCanvas
        {
            get { return (WinoDayTimelineCanvas)GetValue(ActiveCanvasProperty); }
            set { SetValue(ActiveCanvasProperty, value); }
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

        public async void UpdateActiveCanvas()
        {
            if (SelectedIndex < 0)
                ActiveCanvas = null;
            else
            {
                // TODO: Refactor this mechanism by listening to PrepareContainerForItemOverride and Loaded events together.
                while (ContainerFromIndex(SelectedIndex) == null)
                {
                    await Task.Delay(100);
                }

                if (ContainerFromIndex(SelectedIndex) is FlipViewItem flipViewItem)
                {
                    ActiveCanvas = flipViewItem.FindDescendant<WinoDayTimelineCanvas>();
                }
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

        public void NavigateHour(TimeSpan hourTimeSpan)
        {
            // Total height of the FlipViewItem is the same as vertical ScrollViewer to position day headers.
            // Find the day range that contains the hour.
        }

        private ObservableRangeCollection<DayRangeRenderModel> GetItemsSource()
            => ItemsSource as ObservableRangeCollection<DayRangeRenderModel>;
    }
}
