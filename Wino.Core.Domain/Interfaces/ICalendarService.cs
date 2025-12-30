using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

public interface ICalendarService
{
    Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId);
    Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId);
    Task DeleteCalendarItemAsync(Guid calendarItemId);

    Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar);
    Task InsertAccountCalendarAsync(AccountCalendar accountCalendar);
    Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar);
    Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees);
    
    /// <summary>
    /// Retrieves calendar events for a given calendar within the specified time period.
    /// </summary>
    /// <param name="calendar">The calendar to retrieve events from.</param>
    /// <param name="period">The time period to query events for.</param>
    /// <returns>List of calendar items including regular events and recurring event occurrences.</returns>
    Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, ITimePeriod period);

    /// <summary>
    /// Expands a recurring calendar item to check if any of its occurrences fall within the given periods.
    /// </summary>
    /// <param name="calendarItem">The calendar item to expand (can be recurring or non-recurring).</param>
    /// <param name="periods">The list of periods to check against.</param>
    /// <returns>List of calendar items (either the original item or expanded recurrence instances) that fall within the periods.</returns>
    Task<List<CalendarItem>> GetExpandedRecurringEventsForPeriodsAsync(CalendarItem calendarItem, IEnumerable<ITimePeriod> periods);
    
    Task<CalendarItem> GetCalendarItemAsync(Guid accountCalendarId, string remoteEventId);
    Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken);

    /// <summary>
    /// Returns the correct calendar item based on the target details.
    /// </summary>
    /// <param name="targetDetails">Target details.</param>
    Task<CalendarItem> GetCalendarItemTargetAsync(CalendarItemTarget targetDetails);
    Task<CalendarItem> GetCalendarItemAsync(Guid id);
    Task<List<CalendarEventAttendee>> GetAttendeesAsync(Guid calendarEventTrackingId);
    Task<List<CalendarEventAttendee>> ManageEventAttendeesAsync(Guid calendarItemId, List<CalendarEventAttendee> allAttendees);
    Task UpdateCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees);
}
