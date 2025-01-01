using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar
{
    public record CalendarSettings(DayOfWeek FirstDayOfWeek,
                                   List<DayOfWeek> WorkingDays,
                                   TimeSpan WorkingHourStart,
                                   TimeSpan WorkingHourEnd,
                                   double HourHeight,
                                   DayHeaderDisplayType DayHeaderDisplayType,
                                   CultureInfo CultureInfo)
    {
        public TimeSpan? GetTimeSpan(string selectedTime)
        {
            var format = DayHeaderDisplayType switch
            {
                DayHeaderDisplayType.TwelveHour => "h:mm tt",
                DayHeaderDisplayType.TwentyFourHour => "HH:mm",
                _ => throw new ArgumentOutOfRangeException(nameof(DayHeaderDisplayType))
            };

            if (DateTime.TryParseExact(selectedTime, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedTime))
            {
                return parsedTime.TimeOfDay;
            }
            else
            {
                return null;
            }
        }

        public string GetTimeString(TimeSpan timeSpan)
        {
            var format = DayHeaderDisplayType switch
            {
                DayHeaderDisplayType.TwelveHour => "h:mm tt",
                DayHeaderDisplayType.TwentyFourHour => "HH:mm",
                _ => throw new ArgumentOutOfRangeException(nameof(DayHeaderDisplayType))
            };


            var dateTime = DateTime.Today.Add(timeSpan);
            return dateTime.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
