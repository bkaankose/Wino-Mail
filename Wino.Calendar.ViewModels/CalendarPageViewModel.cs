using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels
{
    public partial class CalendarPageViewModel : CalendarBaseViewModel
    {
        [ObservableProperty]
        private ObservableCollection<DayRangeRenderModel> _dayRanges = [];

        public CalendarPageViewModel()
        {
            // Fill whole month days to Dates collection starting from the first day of the month.
            var now = DateTime.Now;

            var firstDayOfMonth = new DateTime(now.Year, now.Month, 1);
            var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

            // Settings

            var workingHourStart = new TimeSpan(8, 0, 0);
            var workingHourEnd = new TimeSpan(16, 0, 0);
            var workingDays = new List<DayOfWeek>() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

            DayHeaderDisplayType dayHeaderDisplayType = DayHeaderDisplayType.TwentyFourHour;

            for (var i = 0; i < lastDayOfMonth.Day; i++)
            {
                if (i % 7 == 0)
                {
                    var startDate = firstDayOfMonth.AddDays(i);
                    var endDate = firstDayOfMonth.AddDays(i + 7);

                    var renderOptions = new CalendarRenderOptions(startDate,
                                              endDate,
                                              workingDays,
                                              workingHourStart,
                                              workingHourEnd,
                                              60,
                                              dayHeaderDisplayType,
                                              new CultureInfo("tr-TR"));

                    DayRanges.Add(new DayRangeRenderModel(renderOptions));
                }
            }
        }
    }
}
