using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICalendarService
    {
        Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId);
        Task DeleteCalendarItemAsync(Guid calendarItemId);

        Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar);
        Task InsertAccountCalendarAsync(AccountCalendar accountCalendar);
        Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar);
        Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees);
        Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, DateTime rangeStart, DateTime rangeEnd);
    }
}
