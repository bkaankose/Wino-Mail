
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.WinUI;
using Itenso.TimePeriod;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Models;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarPanel : Panel
    {
        private const double LastItemRightExtraMargin = 12d;

        // Store each ICalendarItem measurements by their Id.
        private readonly Dictionary<ICalendarItem, CalendarItemMeasurement> _measurements = new Dictionary<ICalendarItem, CalendarItemMeasurement>();

        public static readonly DependencyProperty EventItemMarginProperty = DependencyProperty.Register(nameof(EventItemMargin), typeof(Thickness), typeof(WinoCalendarPanel), new PropertyMetadata(new Thickness(0, 0, 0, 0)));
        public static readonly DependencyProperty HourHeightProperty = DependencyProperty.Register(nameof(HourHeight), typeof(double), typeof(WinoCalendarPanel), new PropertyMetadata(0d));
        public static readonly DependencyProperty PeriodProperty = DependencyProperty.Register(nameof(Period), typeof(ITimePeriod), typeof(WinoCalendarPanel), new PropertyMetadata(null));

        public ITimePeriod Period
        {
            get { return (ITimePeriod)GetValue(PeriodProperty); }
            set { SetValue(PeriodProperty, value); }
        }

        public double HourHeight
        {
            get { return (double)GetValue(HourHeightProperty); }
            set { SetValue(HourHeightProperty, value); }
        }

        public Thickness EventItemMargin
        {
            get { return (Thickness)GetValue(EventItemMarginProperty); }
            set { SetValue(EventItemMarginProperty, value); }
        }

        private void ResetMeasurements() => _measurements.Clear();

        private double GetChildTopMargin(ICalendarItem calendarItemViewModel, double availableHeight)
        {
            var childStart = calendarItemViewModel.StartDate;

            if (childStart <= Period.Start)
            {
                // Event started before or exactly at the periods tart. This might be a multi-day event.
                // We can simply consider event must not have a top margin.

                return 0d;
            }

            double minutesFromStart = (childStart - Period.Start).TotalMinutes;
            return (minutesFromStart / 1440) * availableHeight;
        }

        private double GetChildWidth(CalendarItemMeasurement calendarItemMeasurement, double availableWidth)
        {
            return (calendarItemMeasurement.Right - calendarItemMeasurement.Left) * availableWidth;
        }

        private double GetChildLeftMargin(CalendarItemMeasurement calendarItemMeasurement, double availableWidth)
            => availableWidth * calendarItemMeasurement.Left;

        private double GetChildHeight(ICalendarItem child)
        {
            // All day events are not measured.
            if (child.IsAllDayEvent) return 0;

            double childDurationInMinutes = 0d;
            double availableHeight = HourHeight * 24;

            var periodRelation = child.Period.GetRelation(Period);

            Debug.WriteLine($"Render relation of {child.Title} ({child.Period.Start} - {child.Period.End}) is {periodRelation} with {Period.Start.Day}");

            if (!child.IsMultiDayEvent)
            {
                childDurationInMinutes = child.Period.Duration.TotalMinutes;
            }
            else
            {
                // Multi-day event.
                // Check how many of the event falls into the current period.
                childDurationInMinutes = (child.Period.End - Period.Start).TotalMinutes;
            }

            return (childDurationInMinutes / 1440) * availableHeight;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            ResetMeasurements();
            return base.MeasureOverride(availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Period == null || HourHeight == 0d) return finalSize;

            // Measure/arrange each child height and width.
            // This is a vertical calendar. Therefore the height of each child is the duration of the event.
            // Children weights for left and right will be saved if they don't exist.
            // This is important because we don't want to measure the weights again.
            // They don't change until new event is added or removed.
            // Width of the each child may depend on the rectangle packing algorithm.
            // Children are first categorized into columns. Then each column is shifted to the left until
            // no overlap occurs. The width of each child is calculated based on the number of columns it spans.

            double availableHeight = finalSize.Height;
            double availableWidth = finalSize.Width;

            var calendarControls = Children.Cast<ContentPresenter>();

            if (!calendarControls.Any()) return base.ArrangeOverride(finalSize);

            var events = calendarControls.Select(a => a.Content as CalendarItemViewModel);

            LayoutEvents(events);

            foreach (var control in calendarControls)
            {
                // We can't arrange this child.
                if (!(control.Content is ICalendarItem child)) continue;

                bool isHorizontallyLastItem = false;

                double childWidth = 0,
                    childHeight = Math.Max(0, GetChildHeight(child)),
                    childTop = Math.Max(0, GetChildTopMargin(child, availableHeight)),
                    childLeft = 0;

                // No need to measure anything here.
                if (childHeight == 0) continue;

                if (!_measurements.ContainsKey(child))
                {
                    // Multi-day event.

                    childLeft = 0;
                    childWidth = availableWidth;
                }
                else
                {
                    var childMeasurement = _measurements[child];

                    childWidth = Math.Max(0, GetChildWidth(childMeasurement, finalSize.Width));
                    childLeft = Math.Max(0, GetChildLeftMargin(childMeasurement, availableWidth));

                    isHorizontallyLastItem = childMeasurement.Right == 1;
                }

                // Add additional right margin to items that falls on the right edge of the panel.
                double extraRightMargin = 0;

                // Multi-day events don't have any margin and their hit test is disabled.
                if (!child.IsMultiDayEvent)
                {
                    // Max of 5% of the width or 20px max.
                    extraRightMargin = isHorizontallyLastItem ? Math.Max(LastItemRightExtraMargin, finalSize.Width * 5 / 100) : 0;
                }

                if (childWidth < 0) childWidth = 1;

                var arrangementRect = new Rect(childLeft + EventItemMargin.Left, childTop + EventItemMargin.Top, Math.Max(childWidth - extraRightMargin, 1), childHeight);

                // Make sure measured size will fit in the arranged box.
                var measureSize = arrangementRect.ToSize();
                control.Measure(measureSize);
                control.Arrange(arrangementRect);

                //Debug.WriteLine($"{child.Title}, Measured: {measureSize}, Arranged: {arrangementRect}");
            }

            return finalSize;
        }

        #region ColumSpanning and Packing Algorithm

        private void AddOrUpdateMeasurement(ICalendarItem calendarItem, CalendarItemMeasurement measurement)
        {
            if (_measurements.ContainsKey(calendarItem))
            {
                _measurements[calendarItem] = measurement;
            }
            else
            {
                _measurements.Add(calendarItem, measurement);
            }
        }

        // Pick the left and right positions of each event, such that there are no overlap.
        private void LayoutEvents(IEnumerable<ICalendarItem> events)
        {
            var columns = new List<List<ICalendarItem>>();
            DateTime? lastEventEnding = null;

            foreach (var ev in events.OrderBy(ev => ev.StartDate).ThenBy(ev => ev.EndDate))
            {
                // Multi-day events are not measured.
                if (ev.IsMultiDayEvent) continue;

                if (ev.Period.Start >= lastEventEnding)
                {
                    PackEvents(columns);
                    columns.Clear();
                    lastEventEnding = null;
                }

                bool placed = false;

                foreach (var col in columns)
                {
                    if (!col.Last().Period.OverlapsWith(ev.Period))
                    {
                        col.Add(ev);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    columns.Add(new List<ICalendarItem> { ev });
                }
                if (lastEventEnding == null || ev.Period.End > lastEventEnding.Value)
                {
                    lastEventEnding = ev.Period.End;
                }
            }
            if (columns.Count > 0)
            {
                PackEvents(columns);
            }
        }

        // Set the left and right positions for each event in the connected group.
        private void PackEvents(List<List<ICalendarItem>> columns)
        {
            float numColumns = columns.Count;
            int iColumn = 0;

            foreach (var col in columns)
            {
                foreach (var ev in col)
                {
                    int colSpan = ExpandEvent(ev, iColumn, columns);

                    var leftWeight = iColumn / numColumns;
                    var rightWeight = (iColumn + colSpan) / numColumns;

                    AddOrUpdateMeasurement(ev, new CalendarItemMeasurement(leftWeight, rightWeight));
                }

                iColumn++;
            }
        }

        // Checks how many columns the event can expand into, without colliding with other events.
        private int ExpandEvent(ICalendarItem ev, int iColumn, List<List<ICalendarItem>> columns)
        {
            int colSpan = 1;

            foreach (var col in columns.Skip(iColumn + 1))
            {
                foreach (var ev1 in col)
                {
                    if (ev1.Period.OverlapsWith(ev.Period)) return colSpan;
                }

                colSpan++;
            }

            return colSpan;
        }

        #endregion
    }
}
