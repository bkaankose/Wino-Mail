﻿using System.Collections.Generic;
using System.Linq;
using Itenso.TimePeriod;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Represents a range of days in the calendar.
/// Corresponds to 1 view of the FlipView in CalendarPage.
/// </summary>
public class DayRangeRenderModel
{
    public ITimePeriod Period { get; }
    public List<CalendarDayModel> CalendarDays { get; } = [];

    // TODO: Get rid of this at some point.
    public List<DayHeaderRenderModel> DayHeaders { get; } = [];
    public CalendarRenderOptions CalendarRenderOptions { get; }

    public DayRangeRenderModel(CalendarRenderOptions calendarRenderOptions)
    {
        CalendarRenderOptions = calendarRenderOptions;

        for (var i = 0; i < CalendarRenderOptions.TotalDayCount; i++)
        {
            var representingDate = calendarRenderOptions.DateRange.StartDate.AddDays(i);
            var calendarDayModel = new CalendarDayModel(representingDate, calendarRenderOptions);

            CalendarDays.Add(calendarDayModel);
        }

        Period = new TimeRange(CalendarDays.First().RepresentingDate, CalendarDays.Last().RepresentingDate.AddDays(1));

        // Create day headers based on culture info.

        for (var i = 0; i < 24; i++)
        {
            var representingDate = calendarRenderOptions.DateRange.StartDate.Date.AddHours(i);

            string dayHeader = calendarRenderOptions.CalendarSettings.DayHeaderDisplayType switch
            {
                DayHeaderDisplayType.TwelveHour => representingDate.ToString("h tt", calendarRenderOptions.CalendarSettings.CultureInfo),
                DayHeaderDisplayType.TwentyFourHour => representingDate.ToString("HH", calendarRenderOptions.CalendarSettings.CultureInfo),
                _ => "N/A"
            };

            DayHeaders.Add(new DayHeaderRenderModel(dayHeader, calendarRenderOptions.CalendarSettings.HourHeight));
        }
    }
}
