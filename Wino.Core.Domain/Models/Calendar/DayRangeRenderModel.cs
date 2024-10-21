using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a range of days in the calendar.
    /// Usually it's used for day or week, but supports custom ranges.
    /// </summary>
    public class DayRangeRenderModel
    {
        public List<CalendarDayModel> CalendarDays { get; } = new List<CalendarDayModel>();

        public List<DayHeaderRenderModel> DayHeaders { get; } = new List<DayHeaderRenderModel>();

        public DayRangeRenderModel(CalendarRenderOptions calendarRenderOptions)
        {
            CalendarRenderOptions = calendarRenderOptions;
            TotalDayCount = (calendarRenderOptions.EndDate - calendarRenderOptions.StartDate).Days;

            for (var i = 0; i < TotalDayCount; i++)
            {
                var representingDate = calendarRenderOptions.StartDate.AddDays(i);
                var calendarDayModel = new CalendarDayModel(representingDate);

                CalendarDays.Add(calendarDayModel);
            }

            // Create day headers based on culture info.

            for (var i = 0; i < 24; i++)
            {
                var representingDate = calendarRenderOptions.StartDate.Date.AddHours(i);

                string dayHeader = "N/A";

                switch (calendarRenderOptions.DayHeaderDisplayType)
                {
                    case DayHeaderDisplayType.TwelveHour:
                        dayHeader = representingDate.ToString("h tt", calendarRenderOptions.CultureInfo);
                        break;
                    case DayHeaderDisplayType.TwentyFourHour:
                        dayHeader = representingDate.ToString("HH", calendarRenderOptions.CultureInfo);
                        break;
                    default:
                        break;
                }

                DayHeaders.Add(new DayHeaderRenderModel(dayHeader, calendarRenderOptions.HourHeight));
            }
        }

        public int TotalDayCount { get; }

        public CalendarRenderOptions CalendarRenderOptions { get; }
    }
}
