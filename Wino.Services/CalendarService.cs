using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SqlKata;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Calendar;
using Wino.Services.Extensions;

namespace Wino.Services
{
    public class CalendarService : BaseDatabaseService, ICalendarService
    {
        public CalendarService(IDatabaseService databaseService) : base(databaseService)
        {
        }

        public Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId)
            => Connection.Table<AccountCalendar>().Where(x => x.AccountId == accountId).ToListAsync();

        public async Task InsertAccountCalendarAsync(AccountCalendar accountCalendar)
        {
            await Connection.InsertAsync(accountCalendar);

            WeakReferenceMessenger.Default.Send(new CalendarListAdded(accountCalendar));
        }

        public async Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar)
        {
            await Connection.UpdateAsync(accountCalendar);

            WeakReferenceMessenger.Default.Send(new CalendarListUpdated(accountCalendar));
        }

        public async Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar)
        {
            var deleteCalendarItemsQuery = new Query()
                .From(nameof(CalendarItem))
                .Where(nameof(CalendarItem.CalendarId), accountCalendar.Id)
                .Where(nameof(AccountCalendar.AccountId), accountCalendar.AccountId);

            var rawQuery = deleteCalendarItemsQuery.GetRawQuery();

            await Connection.ExecuteAsync(rawQuery);
            await Connection.DeleteAsync(accountCalendar);

            WeakReferenceMessenger.Default.Send(new CalendarListDeleted(accountCalendar));
        }

        public async Task DeleteCalendarItemAsync(Guid calendarItemId)
        {
            var calendarItem = await Connection.GetAsync<CalendarItem>(calendarItemId);

            if (calendarItem == null) return;

            await Connection.Table<CalendarItem>().DeleteAsync(x => x.Id == calendarItemId);

            WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(calendarItem));
        }

        public Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees)
        {
            return Connection.RunInTransactionAsync((conn) =>
               {
                   conn.Insert(calendarItem);
                   conn.InsertAll(attendees);
               });
        }
    }
}
