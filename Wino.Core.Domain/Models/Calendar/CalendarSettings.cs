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
            // Regardless of the format, we need to parse the time to a TimeSpan.
            // User may list as 14:00 but enters 2:00 PM by input.
            // Be flexible, not annoying.

            if (DateTime.TryParse(selectedTime, out DateTime parsedTime))
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
            // Here we don't need to be flexible cuz we're saving back the value to the combos.
            // They are populated based on the format and must be returned with the format.

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
