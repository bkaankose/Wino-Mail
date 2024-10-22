using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels
{
    public partial class CalendarPageViewModel : CalendarBaseViewModel,
        IRecipient<CalendarInitializeMessage>
    {
        [ObservableProperty]
        private ObservableCollection<DayRangeRenderModel> _dayRanges = [];

        [ObservableProperty]
        private int _selectedDateRangeIndex;

        [ObservableProperty]
        private bool _isCalendarEnabled = true;

        private CalendarSettings _calendarSettings;

        private CalendarDisplayType? _latestRenderedDisplayType;

        public CalendarPageViewModel()
        {
            _calendarSettings = new CalendarSettings()
            {
                DayHeaderDisplayType = DayHeaderDisplayType.TwentyFourHour,
                FirstDayOfWeek = DayOfWeek.Monday,
                WorkingDays = new List<DayOfWeek>() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                WorkingHourStart = new TimeSpan(8, 0, 0),
                WorkingHourEnd = new TimeSpan(16, 0, 0),
                HourHeight = 60,
            };
        }

        // Everytime calendar display type is changed, we must reset the day ranges because the whole view will change.
        private bool ShouldResetDayRanges(CalendarInitializeMessage message)
        {
            return _latestRenderedDisplayType != message.DisplayType;
        }

        public void Receive(CalendarInitializeMessage message)
        {
            Debug.WriteLine($"Visible:  {message.VisibleDateRange.ToString()}");
            if (ShouldResetDayRanges(message))
            {
                DayRanges.Clear();
            }

            var loadDirection = GetLoadDirection(message.DisplayDate);

            if (loadDirection == CalendarLoadDirection.None)
            {
                // Scroll to the selected date.

                Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
                Debug.WriteLine("Scrolling to selected date.");
                return;
            }
            else
            {
                // Remove the date ranges that don't fall into the visible range.

                foreach (var range in DayRanges.ToList())
                {
                    if (range.CalendarDays[0].RepresentingDate < message.VisibleDateRange.StartDate ||
                        range.CalendarDays[range.CalendarDays.Count - 1].RepresentingDate > message.VisibleDateRange.EndDate)
                    {
                        Debug.WriteLine("Removing: " + range.CalendarRenderOptions.DateRange.ToString());
                        DayRanges.Remove(range);
                    }
                }

                RenderDates(message, loadDirection);
            }

            // This is the part we arrange the flip view calendar logic.

            /* Loading for a month of the selected date is fine.
             * If the selected date is in the loaded range, we'll just change the selected flip index to scroll.
             * If the selected date is not in the loaded range:
             * 1. Detect the direction of the scroll.
             * 2. Load the next month.
             * 3. Replace existing month with the new month.
             */

            // 2 things are important: How many items should 1 flip have, and, where we should start loading?


        }

        private void RenderDates(CalendarInitializeMessage message, CalendarLoadDirection direction)
        {
            int eachFlipItemCount = 0;

            var displayType = message.DisplayType;
            var totalDaysToLoad = message.VisibleDateRange.TotalDays;
            var flipLoadStartDate = message.VisibleDateRange.StartDate;

            if (displayType == CalendarDisplayType.Day)
            {
                // We detect how much to load by property.
                eachFlipItemCount = message.DayDisplayCount;
            }
            else if (displayType == CalendarDisplayType.Week)
            {
                eachFlipItemCount = 7;

                // Detect the first day of the week that contains the selected date.
                DayOfWeek firstDayOfWeek = _calendarSettings.FirstDayOfWeek;

                int diff = (7 + (message.DisplayDate.DayOfWeek - _calendarSettings.FirstDayOfWeek)) % 7;

                // Start loading from this date instead of visible date.
                flipLoadStartDate = message.DisplayDate.AddDays(-diff).Date;
            }

            // Create day ranges for each flip item until we reach the total days to load.
            int totalFlipItemCount = (int)Math.Ceiling((double)totalDaysToLoad / eachFlipItemCount);

            List<DayRangeRenderModel> renderModels = new();

            for (int i = 0; i < totalFlipItemCount; i++)
            {
                var startDate = flipLoadStartDate.AddDays(i * eachFlipItemCount);
                var endDate = startDate.AddDays(eachFlipItemCount);

                var range = new DateRange(startDate, endDate);
                var renderOptions = new CalendarRenderOptions(range, _calendarSettings);

                renderModels.Add(new DayRangeRenderModel(renderOptions));
            }

            // Place the new ranges to the correct position.
            if (direction == CalendarLoadDirection.Previous)
            {
                // Revert the items and insert to the beginning.
                renderModels.Reverse();

                foreach (var range in renderModels)
                {
                    DayRanges.Insert(0, range);
                }
            }
            else if (direction == CalendarLoadDirection.Next)
            {
                foreach (var range in renderModels)
                {
                    DayRanges.Add(range);
                }
            }

            _latestRenderedDisplayType = message.DisplayType;

            SortObservableCollectionInPlace(DayRanges);

            Debug.WriteLine($"Flip count: ({DayRanges.Count})");
            foreach (var item in DayRanges)
            {
                Debug.WriteLine($"- {item.CalendarRenderOptions.DateRange.ToString()}");
            }

            Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
        }

        public void SortObservableCollectionInPlace(ObservableCollection<DayRangeRenderModel> collection)
        {
            for (int i = 0; i < collection.Count - 1; i++)
            {
                for (int j = i + 1; j < collection.Count; j++)
                {
                    if (collection[i].CalendarRenderOptions.DateRange.StartDate > collection[j].CalendarRenderOptions.DateRange.StartDate)
                    {
                        (collection[j], collection[i]) = (collection[i], collection[j]);
                    }
                }
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(SelectedDateRangeIndex))
            {

            }
        }

        private CalendarLoadDirection GetLoadDirection(DateTime selectedDate)
        {
            if (DayRanges.Count == 0) return CalendarLoadDirection.Next;

            var firstRange = DayRanges[0];
            var lastRange = DayRanges[DayRanges.Count - 1];

            if (selectedDate < firstRange.CalendarDays[0].RepresentingDate)
            {
                return CalendarLoadDirection.Previous;
            }
            else if (selectedDate > lastRange.CalendarDays[lastRange.CalendarDays.Count - 1].RepresentingDate)
            {
                return CalendarLoadDirection.Next;
            }
            return CalendarLoadDirection.None;
        }
    }
}
