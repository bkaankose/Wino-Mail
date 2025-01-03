﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using SqlKata;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
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

        public async Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees)
        {
            await Connection.RunInTransactionAsync((conn) =>
               {
                   conn.Insert(calendarItem);

                   if (attendees != null)
                   {
                       conn.InsertAll(attendees);
                   }
               });

            WeakReferenceMessenger.Default.Send(new CalendarItemAdded(calendarItem));
        }

        public async Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, DayRangeRenderModel dayRangeRenderModel)
        {
            // TODO: We might need to implement caching here.
            // I don't know how much of the events we'll have in total, but this logic scans all events every time.

            var accountEvents = await Connection.Table<CalendarItem>().Where(x => x.CalendarId == calendar.Id).ToListAsync();
            var result = new List<CalendarItem>();

            foreach (var ev in accountEvents)
            {
                ev.AssignedCalendar = calendar;

                // Parse recurrence rules
                var calendarEvent = new CalendarEvent
                {
                    Start = new CalDateTime(ev.StartDate),
                    End = new CalDateTime(ev.EndDate),
                };

                if (string.IsNullOrEmpty(ev.Recurrence))
                {
                    // No recurrence, only check if we fall into the given period.

                    if (ev.Period.OverlapsWith(dayRangeRenderModel.Period))
                    {
                        // TODO: We overlap, but this might be a multi-day event.
                        // Should we split the events here or in panel?
                        // For now just continue.

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
                    var occurrences = calendarEvent.GetOccurrences(dayRangeRenderModel.Period.Start, dayRangeRenderModel.Period.End);

                    foreach (var occurrence in occurrences)
                    {
                        result.Add(ev);
                    }
                }
            }

            return result;
        }

        public Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId)
            => Connection.GetAsync<AccountCalendar>(accountCalendarId);
    }
}
