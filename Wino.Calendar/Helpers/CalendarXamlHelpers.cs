using System.Linq;
using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Windows.UI.Xaml.Controls.Primitives;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Helpers;

namespace Wino.Calendar.Helpers;

public static class CalendarXamlHelpers
{
    public static CalendarItemViewModel GetFirstAllDayEvent(CalendarEventCollection collection)
        => (CalendarItemViewModel)collection.AllDayEvents.FirstOrDefault();

    /// <summary>
    /// Returns full date + duration info in Event Details page details title.
    /// </summary>
    public static string GetEventDetailsDateString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
    {
        // TODO: This is not correct.
        if (calendarItemViewModel == null || settings == null) return string.Empty;

        var start = calendarItemViewModel.Period.Start;
        var end = calendarItemViewModel.Period.End;

        string timeFormat = settings.DayHeaderDisplayType == DayHeaderDisplayType.TwelveHour ? "h:mm tt" : "HH:mm";
        string dateFormat = settings.DayHeaderDisplayType == DayHeaderDisplayType.TwelveHour ? "dddd, dd MMMM h:mm tt" : "dddd, dd MMMM HH:mm";

        if (calendarItemViewModel.CalendarItem.ItemType == CalendarItemType.MultiDay || calendarItemViewModel.CalendarItem.ItemType == CalendarItemType.MultiDayAllDay)
        {
            return $"{start.ToString($"dd MMMM ddd {timeFormat}", settings.CultureInfo)} - {end.ToString($"dd MMMM ddd {timeFormat}", settings.CultureInfo)}";
        }
        else
        {
            return $"{start.ToString(dateFormat, settings.CultureInfo)} - {end.ToString(timeFormat, settings.CultureInfo)}";
        }
    }

    public static string GetRecurrenceString(CalendarItemViewModel calendarItemViewModel)
    {
        // TODO
        return string.Empty;
    }

    public static string GetDetailsPopupDurationString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
    {
        // TODO
        return string.Empty;
    }

    public static PopupPlacementMode GetDesiredPlacementModeForEventsDetailsPopup(
        CalendarItemViewModel calendarItemViewModel,
        CalendarDisplayType calendarDisplayType)
    {
        if (calendarItemViewModel == null) return PopupPlacementMode.Auto;

        bool isAllDayOrMultiDay = calendarItemViewModel.CalendarItem.ItemType == CalendarItemType.MultiDay ||
            calendarItemViewModel.CalendarItem.ItemType == CalendarItemType.AllDay ||
            calendarItemViewModel.CalendarItem.ItemType == CalendarItemType.MultiDayAllDay;

        // All and/or multi day events always go to the top of the screen.
        if (isAllDayOrMultiDay) return PopupPlacementMode.Bottom;

        return XamlHelpers.GetPlaccementModeForCalendarType(calendarDisplayType);
    }
}
