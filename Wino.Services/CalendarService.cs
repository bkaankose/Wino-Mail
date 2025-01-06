using System;
using System.Collections.Generic;
using System.Linq;
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

            List<CalendarItem> eventsToRemove = new() { calendarItem };

            // In case of parent event, delete all child events as well.
            if (!string.IsNullOrEmpty(calendarItem.Recurrence))
            {
                var recurringEvents = await Connection.Table<CalendarItem>().Where(a => a.RecurringCalendarItemId == calendarItemId).ToListAsync().ConfigureAwait(false);

                eventsToRemove.AddRange(recurringEvents);
            }

            foreach (var @event in eventsToRemove)
            {
                await Connection.Table<CalendarItem>().DeleteAsync(x => x.Id == @event.Id).ConfigureAwait(false);
                await Connection.Table<CalendarEventAttendee>().DeleteAsync(a => a.CalendarItemId == @event.Id).ConfigureAwait(false);

                WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(@event));
            }
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
            // I don't know how much of the events we'll have in total, but this logic scans all events every time for given calendar.

            var accountEvents = await Connection.Table<CalendarItem>()
                .Where(x => x.CalendarId == calendar.Id && !x.IsHidden).ToListAsync();

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
                        result.Add(ev);
                    }
                }
                else
                {
                    // This event has recurrences.
                    // Wino stores exceptional recurrent events as a separate calendar item, without the recurrence rule.
                    // Because each instance of recurrent event can have different attendees, properties etc.
                    // Even though the event is recurrent, each updated instance is a separate calendar item.
                    // Calculate the all recurrences, and remove the exceptional instances like hidden ones.

                    var recurrenceLines = Regex.Split(ev.Recurrence, Constants.CalendarEventRecurrenceRuleSeperator);

                    foreach (var line in recurrenceLines)
                    {
                        calendarEvent.RecurrenceRules.Add(new RecurrencePattern(line));
                    }

                    // Calculate occurrences in the range.
                    var occurrences = calendarEvent.GetOccurrences(dayRangeRenderModel.Period.Start, dayRangeRenderModel.Period.End);

                    // Get all recurrent exceptional calendar events.
                    var exceptionalRecurrences = await Connection.Table<CalendarItem>()
                        .Where(a => a.RecurringCalendarItemId == ev.Id)
                        .ToListAsync()
                        .ConfigureAwait(false);

                    foreach (var occurrence in occurrences)
                    {
                        var exactInstanceCheck = exceptionalRecurrences.FirstOrDefault(a =>
                        a.Period.OverlapsWith(dayRangeRenderModel.Period));

                        if (exactInstanceCheck == null)
                        {
                            // There is no exception for the period.
                            // Change the instance StartDate and Duration.

                            ev.StartDate = occurrence.Period.StartTime.Value;
                            ev.DurationInSeconds = (occurrence.Period.EndTime.Value - occurrence.Period.StartTime.Value).TotalSeconds;

                            result.Add(ev);
                        }
                        else
                        {
                            // There is a single instance of this recurrent event.
                            // It will be added as single item if it's not hidden.
                            // We don't need to do anything here.
                        }
                    }
                }
            }

            return result;
        }

        public Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId)
            => Connection.GetAsync<AccountCalendar>(accountCalendarId);

        public async Task<CalendarItem> GetCalendarItemAsync(Guid accountCalendarId, string remoteEventId)
        {
            var query = new Query()
                .From(nameof(CalendarItem))
                .Where(nameof(CalendarItem.CalendarId), accountCalendarId)
                .Where(nameof(CalendarItem.RemoteEventId), remoteEventId);

            var rawQuery = query.GetRawQuery();

            var calendarItem = await Connection.FindWithQueryAsync<CalendarItem>(rawQuery);

            // Load assigned calendar.
            if (calendarItem != null)
            {
                calendarItem.AssignedCalendar = await Connection.GetAsync<AccountCalendar>(calendarItem.CalendarId);
            }

            return calendarItem;
        }
    }
}
