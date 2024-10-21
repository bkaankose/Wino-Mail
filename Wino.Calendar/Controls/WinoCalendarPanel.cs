
using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarPanel : Panel
    {
        private DateTime? currentStartDate;
        private DateTime? currentEndDate;

        public CalendarDayModel DayModel
        {
            get { return (CalendarDayModel)GetValue(DayModelProperty); }
            set { SetValue(DayModelProperty, value); }
        }

        public static readonly DependencyProperty DayModelProperty = DependencyProperty.Register(nameof(DayModel), typeof(CalendarDayModel), typeof(WinoCalendarPanel), new PropertyMetadata(null, new PropertyChangedCallback(OnDayChanged)));

        private static void OnDayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WinoCalendarPanel control)
            {
                control.SetDates();
                control.UpdateLayout();
            }
        }

        private void SetDates()
        {
            if (DayModel != null)
            {
                currentStartDate = DayModel.RepresentingDate;
                currentEndDate = DayModel.RepresentingDate.AddDays(1);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children)
            {
                if (child is CalendarItemControl calendarItemControl &&
                    calendarItemControl.Item?.StartTime >= currentStartDate &&
                    calendarItemControl.Item?.EndTime <= currentEndDate)
                {
                    // Calculate how much of space this item needs.

                    // TODO: Horizontally conflicting events must share the available horizontal space.
                    // For now they all take the same width of all space.

                    var itemDurationBasedOnStartDate = calendarItemControl.Item.StartTime - currentStartDate.Value;

                    // Event started earlier than the start date of the calendar.
                    // TODO: Calculate when it ends.
                    if (itemDurationBasedOnStartDate.TotalDays < 0)
                    {

                    }
                    else if (itemDurationBasedOnStartDate.TotalDays > 1)
                    {
                        // TODO: Item occupies the whole day.
                        // Make sure it occupies the whole height of the day.

                        child.Measure(availableSize);
                    }
                    else
                    {
                        // Calculate the height based on the duration of the event.
                        var totalHeight = availableSize.Height;

                        var itemMinutes = itemDurationBasedOnStartDate.TotalMinutes;
                        var itemHeight = itemMinutes / 1440 * totalHeight;

                        child.Measure(new Size(availableSize.Width, itemHeight));
                    }
                }
            }

            return base.MeasureOverride(availableSize);
        }

        // Method to calculate the top position of a child based on its start time
        private double CalculateChildTopPosition(DateTime childStart, double availableHeight)
        {
            double totalMinutes = 1440;
            double minutesFromStart = (childStart - DayModel.RepresentingDate).TotalMinutes;
            return (minutesFromStart / totalMinutes) * availableHeight;
        }

        // Method to calculate the height of a child based on its date range
        private double CalculateChildHeight(DateTime childStart, DateTime childEnd, double availableHeight)
        {
            double totalMinutes = 1440;
            double childDuration = (childEnd - childStart).TotalMinutes;
            return (childDuration / totalMinutes) * availableHeight;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double availableHeight = finalSize.Height;

            foreach (UIElement child in Children)
            {
                DateTime? childStart = GetChildStartDate(child);
                DateTime? childEnd = GetChildEndDate(child);

                // Not ready to render.
                if (childStart == null || childEnd == null) continue;

                if (childStart >= currentStartDate && childEnd <= currentEndDate)
                {
                    double childHeight = CalculateChildHeight(childStart.Value, childEnd.Value, availableHeight);
                    double childTop = CalculateChildTopPosition(childStart.Value, availableHeight);

                    // Arrange child at the calculated position
                    child.Arrange(new Rect(0, childTop, finalSize.Width, childHeight));
                }
            }

            return finalSize;
        }

        private DateTime? GetChildStartDate(UIElement child)
        {
            if (child is CalendarItemControl calendarItemControl && calendarItemControl.Item != null)
                return calendarItemControl.Item.StartTime;

            return default;
        }

        private DateTime? GetChildEndDate(UIElement child)
        {
            if (child is CalendarItemControl calendarItemControl && calendarItemControl.Item != null)
                return calendarItemControl.Item.EndTime;

            return default;
        }
    }
}
