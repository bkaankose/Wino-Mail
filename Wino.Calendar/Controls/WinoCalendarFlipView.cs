using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.MenuItems;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarFlipView : CustomCalendarFlipView
    {
        public event EventHandler<WinoDayTimelineCanvas> ActiveTimelineCanvasChanged;

        public WinoCalendarFlipView()
        {
            SelectionChanged += CalendarDisplayRangeChanged;
        }

        private async void CalendarDisplayRangeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedIndex < 0)
                ActiveTimelineCanvasChanged?.Invoke(this, null);
            else
            {
                // TODO: Refactor this mechanism by listening to PrepareContainerForItemOverride and Loaded events together.
                while (ContainerFromIndex(SelectedIndex) == null)
                {
                    await Task.Delay(250);
                }

                if (ContainerFromIndex(SelectedIndex) is FlipViewItem flipViewItem)
                {
                    var canvas = flipViewItem.FindDescendant<WinoDayTimelineCanvas>();
                    ActiveTimelineCanvasChanged?.Invoke(this, canvas);
                }
            }
        }

        /// <summary>
        /// Navigates to the specified date in the calendar.
        /// </summary>
        /// <param name="dateTime">Date to navigate.</param>
        public async void NavigateToDay(DateTime dateTime)
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
