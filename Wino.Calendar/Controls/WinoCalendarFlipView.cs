using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarFlipView : FlipView
    {
        public event EventHandler<WinoDayTimelineCanvas> ActiveTimelineCanvasChanged;

        private const string PART_PreviousButton = "PreviousButtonHorizontal";
        private const string PART_NextButton = "NextButtonHorizontal";

        private Button PreviousButton;
        private Button NextButton;

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

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            PreviousButton = GetTemplateChild(PART_PreviousButton) as Button;
            NextButton = GetTemplateChild(PART_NextButton) as Button;

            // Hide navigation buttons
            //PreviousButton.Opacity = NextButton.Opacity = 0;
            //PreviousButton.IsHitTestVisible = NextButton.IsHitTestVisible = false;
        }

        /// <summary>
        /// Navigates to the specified date in the calendar.
        /// </summary>
        /// <param name="dateTime">Date to navigate.</param>
        public void NavigateToDay(DateTime dateTime)
        {
            // Find the day range that contains the date.
            var dayRange = GetItemsSource()?.FirstOrDefault(a => a.CalendarDays.Any(b => b.RepresentingDate.Date == dateTime.Date));

            if (dayRange != null)
            {
                var navigationItemIndex = GetItemsSource().IndexOf(dayRange);

                // Until we reach the day in the flip, simulate next-prev button clicks.
                // This will make sure the FlipView animations are triggered.
                // Setting SelectedIndex directly doesn't trigger the animations.

                while (SelectedIndex != navigationItemIndex)
                {
                    if (SelectedIndex > navigationItemIndex)
                    {
                        // Go back.
                        var backPeer = new ButtonAutomationPeer(PreviousButton);
                        backPeer.Invoke();
                    }
                    else
                    {
                        // Go forward.
                        var nextPeer = new ButtonAutomationPeer(NextButton);
                        nextPeer.Invoke();
                    }
                }
            }
        }

        public void NavigateHour(TimeSpan hourTimeSpan)
        {
            // Total height of the FlipViewItem is the same as vertical ScrollViewer to position day headers.
            // Find the day range that contains the hour.


        }

        private ObservableCollection<DayRangeRenderModel> GetItemsSource()
            => ItemsSource as ObservableCollection<DayRangeRenderModel>;
    }
}
