using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Wino.Core.Domain.Models.Calendar;
using Wino.Extensions;
using Wino.Helpers;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarView : Control
    {
        private const string PART_DayViewItemBorder = nameof(PART_DayViewItemBorder);
        private const string PART_CalendarView = nameof(PART_CalendarView);

        public static readonly DependencyProperty HighlightedDateRangeProperty = DependencyProperty.Register(nameof(HighlightedDateRange), typeof(DateRange), typeof(WinoCalendarView), new PropertyMetadata(null, new PropertyChangedCallback(OnPropertiesChanged)));
        public static readonly DependencyProperty VisibleDateBackgroundProperty = DependencyProperty.Register(nameof(VisibleDateBackground), typeof(Brush), typeof(WinoCalendarView), new PropertyMetadata(null, new PropertyChangedCallback(OnPropertiesChanged)));
        public static readonly DependencyProperty TodayBackgroundBrushProperty = DependencyProperty.Register(nameof(TodayBackgroundBrush), typeof(Brush), typeof(WinoCalendarView), new PropertyMetadata(null));
        public static readonly DependencyProperty DateClickedCommandProperty = DependencyProperty.Register(nameof(DateClickedCommand), typeof(ICommand), typeof(WinoCalendarView), new PropertyMetadata(null));
        public static readonly DependencyProperty DisplayDateProperty = DependencyProperty.Register(nameof(DisplayDate), typeof(DateTimeOffset), typeof(WinoCalendarView), new PropertyMetadata(default(DateTimeOffset), new PropertyChangedCallback(OnDisplayDateChanged)));
        public static readonly DependencyProperty BoundriesDateRangeProperty = DependencyProperty.Register(nameof(BoundriesDateRange), typeof(DateRange), typeof(WinoCalendarView), new PropertyMetadata(null));

        /// <summary>
        /// Gets or sets the first and last dates that are visible on the view.
        /// </summary>
        public DateRange BoundriesDateRange
        {
            get { return (DateRange)GetValue(BoundriesDateRangeProperty); }
            set { SetValue(BoundriesDateRangeProperty, value); }
        }

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
                control.ScrollToDisplayDate();

            }
        }

        private async void ScrollToDisplayDate()
        {
            if (DisplayDate == default) return;

            // When a date is clicked, try to scroll to it.
            // TODO: This logic can be changed to dispaly +1/-1 rows of the clicked date.

            var monthScrollViewer = WinoVisualTreeHelper.GetChildObject<ScrollViewer>(CalendarView, "MonthViewScrollViewer");

            if (monthScrollViewer != null)
            {
                var markDateCalendarDayItems = WinoVisualTreeHelper.FindDescendants<CalendarViewDayItem>(CalendarView);

                var dayitem = markDateCalendarDayItems.FirstOrDefault(a => a.Date.Date == DisplayDate.Date);

                if (dayitem != null)
                {
                    //monthScrollViewer.ScrollToElement(dayitem, addMargin: false);

                    // Add small delay until the scroll animation ends.
                    // await Task.Delay(1);
                    await Task.Yield();

                    var boundryVisibleDates = GetVisibleDates(monthScrollViewer, markDateCalendarDayItems);

                    BoundriesDateRange = new DateRange(boundryVisibleDates.StartDate.DateTime, boundryVisibleDates.EndDate.DateTime);

                    var args = new CalendarViewDayClickedEventArgs(BoundriesDateRange, DisplayDate.DateTime);
                    DateClickedCommand?.Execute(args);
                }
            }
        }

        private void InternalCalendarViewSelectionChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (args.AddedDates?.Count > 0)
            {
                var clickedDate = args.AddedDates[0].Date;

                CalendarView.SetDisplayDate(clickedDate);

                DisplayDate = clickedDate;
            }

            // Reset selection, we don't show selected dates but react to them.
            CalendarView.SelectedDates.Clear();
        }

        private (DateTimeOffset StartDate, DateTimeOffset EndDate) GetVisibleDates(ScrollViewer scrollViewer, IEnumerable<CalendarViewDayItem> realizedItems)
        {
            var firstVisibleItem = realizedItems.FirstOrDefault(a => a.IsFullyVisibile(scrollViewer));
            if (firstVisibleItem == null)
            {
                return (DateTimeOffset.MinValue, DateTimeOffset.MinValue);
            }
            var lastVisibleItem = realizedItems.LastOrDefault(a => a.IsFullyVisibile(scrollViewer));
            return (firstVisibleItem.Date, lastVisibleItem.Date);
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
