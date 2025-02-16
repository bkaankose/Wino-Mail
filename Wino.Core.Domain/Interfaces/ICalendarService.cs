using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, DayRangeRenderModel dayRangeRenderModel);
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
}
