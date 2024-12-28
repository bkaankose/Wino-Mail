using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Ical.Net.DataTypes;
using SqlKata;
using Wino.Core.Domain;
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

        public async Task<List<ICalendarItem>> GetCalendarEventsAsync(Guid calendarId, DateTime rangeStart, DateTime rangeEnd)
        {
            // TODO: We might need to implement caching here.
            // I don't know how much of the events we'll have in total, but this logic scans all events every time.

            var accountEvents = await Connection.Table<CalendarItem>().Where(x => x.CalendarId == calendarId).ToListAsync();
            var result = new List<ICalendarItem>();

            foreach (var ev in accountEvents)
            {
                // Parse recurrence rules
                var calendarEvent = new Ical.Net.CalendarComponents.CalendarEvent
                {
                    Start = new CalDateTime(ev.StartTime.UtcDateTime),
                    Duration = TimeSpan.FromMinutes(ev.DurationInMinutes),
                };

                if (string.IsNullOrEmpty(ev.Recurrence))
                {
                    // No recurrence, only check if we fall into the date range.
                    // All events are saved in UTC, so we need to convert the range to UTC as well.
                    if (ev.StartTime.UtcDateTime < rangeEnd
                        && ev.StartTime.UtcDateTime.AddMinutes(ev.DurationInMinutes) > rangeStart)
                    {
                        result.Add(ev);
                    }
                }
                else
                {
                    var recurrenceLines = Regex.Split(ev.Recurrence, Constants.CalendarEventRecurrenceRuleSeperator);

                    foreach (var line in recurrenceLines)
                    {
                        calendarEvent.RecurrenceRules.Add(new RecurrencePattern(line));
                    }

                    // Calculate occurrences in the range.
                    var occurrences = calendarEvent.GetOccurrences(rangeStart, rangeEnd);

                    foreach (var occurrence in occurrences)
                    {
                        result.Add(ev);
                    }
                }
            }

            return result;
        }
    }
}
