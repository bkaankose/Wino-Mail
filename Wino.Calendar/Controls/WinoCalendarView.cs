using System;
using System.Linq;
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

        public static readonly DependencyProperty VisibleDateRangeProperty = DependencyProperty.Register(nameof(VisibleDateRange), typeof(DateRange), typeof(WinoCalendarView), new PropertyMetadata(null, new PropertyChangedCallback(OnPropertiesChanged)));
        public static readonly DependencyProperty VisibleDateBackgroundProperty = DependencyProperty.Register(nameof(VisibleDateBackground), typeof(Brush), typeof(WinoCalendarView), new PropertyMetadata(null, new PropertyChangedCallback(OnPropertiesChanged)));
        public static readonly DependencyProperty TodayBackgroundBrushProperty = DependencyProperty.Register(nameof(TodayBackgroundBrush), typeof(Brush), typeof(WinoCalendarView), new PropertyMetadata(null));
        public static readonly DependencyProperty DateClickedCommandProperty = DependencyProperty.Register(nameof(DateClickedCommand), typeof(ICommand), typeof(WinoCalendarView), new PropertyMetadata(null));

        public ICommand DateClickedCommand
        {
            get { return (ICommand)GetValue(DateClickedCommandProperty); }
            set { SetValue(DateClickedCommandProperty, value); }
        }

        public DateRange VisibleDateRange
        {
            get { return (DateRange)GetValue(VisibleDateRangeProperty); }
            set { SetValue(VisibleDateRangeProperty, value); }
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

        private void InternalCalendarViewSelectionChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (args.AddedDates?.Count > 0)
            {
                var clickedDate = args.AddedDates[0].Date;

                DateClickedCommand?.Execute(clickedDate);

                // When a date is clicked, try to scroll to it.
                // TODO: This logic can be changed to dispaly +1/-1 rows of the clicked date.

                var monthScrollViewer = WinoVisualTreeHelper.GetChildObject<ScrollViewer>(CalendarView, "MonthViewScrollViewer");

                if (monthScrollViewer != null)
                {
                    var markDateCalendarDayItems = WinoVisualTreeHelper.FindDescendants<CalendarViewDayItem>(CalendarView);

                    var dayitem = markDateCalendarDayItems.FirstOrDefault(a => a.Date.Date == clickedDate);

                    if (dayitem != null)
                    {
                        monthScrollViewer.ScrollToElement(dayitem);
                    }
                }
            }

            // Reset selection, we don't show selected dates but react to them.
            CalendarView.SelectedDates.Clear();
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
            if (VisibleDateRange == null || VisibleDateBackground == null || TodayBackgroundBrush == null || CalendarView == null) return;

            var markDateCalendarDayItems = WinoVisualTreeHelper.FindDescendants<CalendarViewDayItem>(CalendarView);

            foreach (var calendarDayItem in markDateCalendarDayItems)
            {
                var border = WinoVisualTreeHelper.GetChildObject<Border>(calendarDayItem, PART_DayViewItemBorder);

                if (border == null) return;

                if (calendarDayItem.Date.Date == DateTime.Today.Date)
                {
                    border.Background = TodayBackgroundBrush;
                }
                else if (calendarDayItem.Date.Date >= VisibleDateRange.StartDate.Date && calendarDayItem.Date.Date < VisibleDateRange.EndDate.Date)
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
