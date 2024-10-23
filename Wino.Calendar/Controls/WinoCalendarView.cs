using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Wino.Core.Domain.Models.Calendar;
using Wino.Helpers;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarView : Control
    {
        private const string PART_MonthViewScrollViewer = "MonthViewScrollViewer";
        private const string PART_DayViewItemBorder = nameof(PART_DayViewItemBorder);
        private const string PART_CalendarView = nameof(PART_CalendarView);

        public static readonly DependencyProperty HighlightedDateRangeProperty = DependencyProperty.Register(nameof(HighlightedDateRange), typeof(DateRange), typeof(WinoCalendarView), new PropertyMetadata(null, new PropertyChangedCallback(OnPropertiesChanged)));
        public static readonly DependencyProperty VisibleDateBackgroundProperty = DependencyProperty.Register(nameof(VisibleDateBackground), typeof(Brush), typeof(WinoCalendarView), new PropertyMetadata(null, new PropertyChangedCallback(OnPropertiesChanged)));
        public static readonly DependencyProperty TodayBackgroundBrushProperty = DependencyProperty.Register(nameof(TodayBackgroundBrush), typeof(Brush), typeof(WinoCalendarView), new PropertyMetadata(null));
        public static readonly DependencyProperty DateClickedCommandProperty = DependencyProperty.Register(nameof(DateClickedCommand), typeof(ICommand), typeof(WinoCalendarView), new PropertyMetadata(null));
        public static readonly DependencyProperty DisplayDateProperty = DependencyProperty.Register(nameof(DisplayDate), typeof(DateTimeOffset), typeof(WinoCalendarView), new PropertyMetadata(default(DateTimeOffset), new PropertyChangedCallback(OnDisplayDateChanged)));

        /// <summary>
        /// Gets or sets the last clicked date.
        /// </summary>
        public DateTimeOffset DisplayDate
        {
            get { return (DateTimeOffset)GetValue(DisplayDateProperty); }
            set { SetValue(DisplayDateProperty, value); }
        }

        /// <summary>
        /// Gets or sets the command to execute when a date is picked.
        /// Unused.
        /// </summary>
        public ICommand DateClickedCommand
        {
            get { return (ICommand)GetValue(DateClickedCommandProperty); }
            set { SetValue(DateClickedCommandProperty, value); }
        }

        /// <summary>
        /// Gets or sets the highlighted range of dates.
        /// </summary>
        public DateRange HighlightedDateRange
        {
            get { return (DateRange)GetValue(HighlightedDateRangeProperty); }
            set { SetValue(HighlightedDateRangeProperty, value); }
        }

        public Brush VisibleDateBackground
        {
            get { return (Brush)GetValue(VisibleDateBackgroundProperty); }
            set { SetValue(VisibleDateBackgroundProperty, value); }
        }

        public Brush TodayBackgroundBrush
        {
            get { return (Brush)GetValue(TodayBackgroundBrushProperty); }
            set { SetValue(TodayBackgroundBrushProperty, value); }
        }

        private CalendarView CalendarView;

        public WinoCalendarView()
        {
            DefaultStyleKey = typeof(WinoCalendarView);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            CalendarView = GetTemplateChild(PART_CalendarView) as CalendarView;

            Guard.IsNotNull(CalendarView, nameof(CalendarView));

            CalendarView.SelectedDatesChanged -= InternalCalendarViewSelectionChanged;
            CalendarView.SelectedDatesChanged += InternalCalendarViewSelectionChanged;

            // TODO: Should come from settings.
            CalendarView.FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday;

            // Everytime display mode changes, update the visible date range backgrounds.
            // If users go back from year -> month -> day, we need to update the visible date range backgrounds.

            CalendarView.RegisterPropertyChangedCallback(CalendarView.DisplayModeProperty, (s, e) => UpdateVisibleDateRangeBackgrounds());
        }

        private static void OnDisplayDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WinoCalendarView control)
            {
                control.SetDisplayDate((DateTimeOffset)e.NewValue);
                control.ScrollToDisplayDate();
            }
        }

        private async void ScrollToDisplayDate()
        {
            if (DisplayDate == default || CalendarView == null) return;

            // When a date is clicked, try to scroll to it.
            // TODO: This logic can be changed to dispaly +1/-1 rows of the clicked date.

            var monthScrollViewer = WinoVisualTreeHelper.GetChildObject<ScrollViewer>(CalendarView, PART_MonthViewScrollViewer);

            if (monthScrollViewer != null)
            {
                var markDateCalendarDayItems = WinoVisualTreeHelper.FindDescendants<CalendarViewDayItem>(CalendarView);

                var dayitem = markDateCalendarDayItems.FirstOrDefault(a => a.Date.Date == DisplayDate.Date);

                if (dayitem != null)
                {
                    // monthScrollViewer.ScrollToElement(dayitem, addMargin: false);

                    // Add small delay until the scroll animation ends.
                    // await Task.Delay(500);
                    await Task.Yield();

                    // Add +15/-15 days to the display date.

                    // TODO: GetVisibleDates(monthScrollViewer, markDateCalendarDayItems);
                    //var boundryVisibleDates = GetVisibleDates(monthScrollViewer, markDateCalendarDayItems);

                    //var boundryVisibleDates = DateTimeExtensions.GetMonthDateRangeStartingWeekday(DisplayDate.DateTime, (DayOfWeek)CalendarView.FirstDayOfWeek);

                    //BoundriesDateRange = new DateRange(boundryVisibleDates.StartDate, boundryVisibleDates.EndDate);

                    var args = new CalendarViewDayClickedEventArgs(DisplayDate.DateTime);
                    DateClickedCommand?.Execute(args);
                }
            }
        }

        private void InternalCalendarViewSelectionChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (args.AddedDates?.Count > 0)
            {
                var clickedDate = args.AddedDates[0].Date;

                SetDisplayDate(clickedDate);
            }

            // Reset selection, we don't show selected dates but react to them.
            CalendarView.SelectedDates.Clear();
        }

        private void SetDisplayDate(DateTimeOffset date)
        {
            CalendarView.SetDisplayDate(date);

            DisplayDate = date;
        }

        private static void OnPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WinoCalendarView control)
            {
                control.UpdateVisibleDateRangeBackgrounds();
            }
        }

        public void UpdateVisibleDateRangeBackgrounds()
        {
            if (HighlightedDateRange == null || VisibleDateBackground == null || TodayBackgroundBrush == null || CalendarView == null) return;

            var markDateCalendarDayItems = WinoVisualTreeHelper.FindDescendants<CalendarViewDayItem>(CalendarView);

            foreach (var calendarDayItem in markDateCalendarDayItems)
            {
                var border = WinoVisualTreeHelper.GetChildObject<Border>(calendarDayItem, PART_DayViewItemBorder);

                if (border == null) return;

                if (calendarDayItem.Date.Date == DateTime.Today.Date)
                {
                    border.Background = TodayBackgroundBrush;
                }
                else if (calendarDayItem.Date.Date >= HighlightedDateRange.StartDate.Date && calendarDayItem.Date.Date < HighlightedDateRange.EndDate.Date)
                {
                    border.Background = VisibleDateBackground;
                }
                else
                {
                    border.Background = null;
                }
            }
        }
    }
}
