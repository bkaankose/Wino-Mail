using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels
{
    public partial class CalendarPageViewModel : CalendarBaseViewModel,
        IRecipient<CalendarDateClickedMessage>
    {
        [ObservableProperty]
        private ObservableCollection<DayRangeRenderModel> _dayRanges = [];

        [ObservableProperty]
        private int _selectedDateRangeIndex;

        [ObservableProperty]
        private int _dayLoadCount = 7;

        [ObservableProperty]
        private CalendarDisplayType _calendarDisplayType;

        private CalendarSettings _calendarSettings;

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


            // Fill whole month days to Dates collection starting from the first day of the month.
            var now = DateTime.Now;

            var firstDayOfMonth = new DateTime(now.Year, now.Month, 1);
            var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

            // Settings

            //var workingHourStart = new TimeSpan(8, 0, 0);
            //var workingHourEnd = new TimeSpan(16, 0, 0);
            //var workingDays = new List<DayOfWeek>() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

            //DayHeaderDisplayType dayHeaderDisplayType = DayHeaderDisplayType.TwentyFourHour;

            for (var i = 0; i < lastDayOfMonth.Day; i++)
            {
                if (i % 7 == 0)
                {
                    var range = new DateRange(firstDayOfMonth.AddDays(i), firstDayOfMonth.AddDays(i + 7));

                    var renderOptions = new CalendarRenderOptions(range, _calendarSettings);

                    DayRanges.Add(new DayRangeRenderModel(renderOptions));
                }
            }
        }

        public void Receive(CalendarDateClickedMessage message)
        {
            // This is the part we arrange the flip view calendar logic.

            /* Loading for a month of the selected date is fine.
             * If the selected date is in the loaded range, we'll just change the selected flip index to scroll.
             * If the selected date is not in the loaded range:
             * 1. Detect the direction of the scroll.
             * 2. Load the next month.
             * 3. Replace existing month with the new month.
             */

            var selectedDate = message.ClickedDate;

            // 2 things are important: How many items should 1 flip have, and, where we should start loading?

            int flipViewLoadCount = 0;

            if (CalendarDisplayType == CalendarDisplayType.Day)
            {
                // We detect how much to load by property.

            }


            //var existingDateRange = DayRanges.FirstOrDefault(a => a.CalendarDays.Any(b => b.RepresentingDate.Date == selectedDate.Date));

            //if (existingDateRange)
            //{

            //}
        }
    }
}
