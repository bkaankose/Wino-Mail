using System.Linq;
using Windows.UI.Xaml.Controls.Primitives;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Helpers;

namespace Wino.Calendar.Helpers
{
    public static class CalendarXamlHelpers
    {
        public static CalendarItemViewModel GetFirstAllDayEvent(CalendarEventCollection collection)
            => (CalendarItemViewModel)collection.AllDayEvents.FirstOrDefault();

        public static string GetDetailsPopupDurationString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
        {
            if (calendarItemViewModel == null || settings == null) return string.Empty;

            // Single event in a day.
            if (!calendarItemViewModel.IsAllDayEvent && !calendarItemViewModel.IsMultiDayEvent)
            {
                return $"{calendarItemViewModel.Period.Start.ToString("d", settings.CultureInfo)} {settings.GetTimeString(calendarItemViewModel.Period.Duration)}";
            }
            else if (calendarItemViewModel.IsMultiDayEvent)
            {
                return $"{calendarItemViewModel.Period.Start.ToString("d", settings.CultureInfo)} - {calendarItemViewModel.Period.End.ToString("d", settings.CultureInfo)}";
            }
            else
            {
                // All day event.
                return $"{calendarItemViewModel.Period.Start.ToString("d", settings.CultureInfo)} ({Translator.CalendarItemAllDay})";
            }
        }

        public static PopupPlacementMode GetDesiredPlacementModeForEventsDetailsPopup(
            CalendarItemViewModel calendarItemViewModel,
            CalendarDisplayType calendarDisplayType)
        {
            if (calendarItemViewModel == null) return PopupPlacementMode.Auto;

            // All and/or multi day events always go to the top of the screen.
            if (calendarItemViewModel.IsAllDayEvent || calendarItemViewModel.IsMultiDayEvent) return PopupPlacementMode.Bottom;

            return XamlHelpers.GetPlaccementModeForCalendarType(calendarDisplayType);
        }
    }
}
