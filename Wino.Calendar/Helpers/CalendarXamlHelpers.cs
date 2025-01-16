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

namespace Wino.Calendar.Helpers
{
    public static class CalendarXamlHelpers
    {
        public static CalendarItemViewModel GetFirstAllDayEvent(CalendarEventCollection collection)
            => (CalendarItemViewModel)collection.AllDayEvents.FirstOrDefault();

        /// <summary>
        /// Returns full date + duration info in Event Details page details title.
        /// </summary>
        public static string GetEventDetailsDateString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
        {
            if (calendarItemViewModel == null || settings == null) return string.Empty;

            var start = calendarItemViewModel.Period.Start;
            var end = calendarItemViewModel.Period.End;

            string timeFormat = settings.DayHeaderDisplayType == DayHeaderDisplayType.TwelveHour ? "h:mm tt" : "HH:mm";
            string dateFormat = settings.DayHeaderDisplayType == DayHeaderDisplayType.TwelveHour ? "dddd, dd MMMM h:mm tt" : "dddd, dd MMMM HH:mm";

            if (calendarItemViewModel.IsMultiDayEvent)
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
            if (calendarItemViewModel == null || !calendarItemViewModel.IsRecurringChild) return string.Empty;

            // Parse recurrence rules
            var calendarEvent = new CalendarEvent
            {
                Start = new CalDateTime(calendarItemViewModel.StartDate),
                End = new CalDateTime(calendarItemViewModel.EndDate),
            };

            var recurrenceLines = Regex.Split(calendarItemViewModel.CalendarItem.Recurrence, Constants.CalendarEventRecurrenceRuleSeperator);

            foreach (var line in recurrenceLines)
            {
                calendarEvent.RecurrenceRules.Add(new RecurrencePattern(line));
            }

            if (calendarEvent.RecurrenceRules == null || !calendarEvent.RecurrenceRules.Any())
            {
                return "No recurrence pattern.";
            }

            var recurrenceRule = calendarEvent.RecurrenceRules.First();
            var daysOfWeek = string.Join(", ", recurrenceRule.ByDay.Select(day => day.DayOfWeek.ToString()));
            string timeZone = calendarEvent.DtStart.TzId ?? "UTC";

            return $"Every {daysOfWeek}, effective {calendarEvent.DtStart.Value.ToShortDateString()} " +
                   $"from {calendarEvent.DtStart.Value.ToShortTimeString()} to {calendarEvent.DtEnd.Value.ToShortTimeString()} " +
                   $"{timeZone}.";
        }

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
