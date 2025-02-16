using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using SqlKata;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;
using Wino.Services.Extensions;

namespace Wino.Services;

public class CalendarService : BaseDatabaseService, ICalendarService
{
    public CalendarService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId)
        => Connection.Table<AccountCalendar>().Where(x => x.AccountId == accountId).OrderByDescending(a => a.IsPrimary).ToListAsync();

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

                        var recurrence = ev.CreateRecurrence(occurrence.Period.StartTime.Value, occurrence.Period.Duration.TotalSeconds);

                        result.Add(recurrence);
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

    public Task<CalendarItem> GetCalendarItemAsync(Guid id)
    {
        var query = new Query()
            .From(nameof(CalendarItem))
            .Where(nameof(CalendarItem.Id), id);

        var rawQuery = query.GetRawQuery();
        return Connection.FindWithQueryAsync<CalendarItem>(rawQuery);
    }

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

    public Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken)
    {
        var query = new Query()
            .From(nameof(AccountCalendar))
            .Where(nameof(AccountCalendar.Id), calendarId)
            .AsUpdate(new { SynchronizationDeltaToken = deltaToken });

        return Connection.ExecuteAsync(query.GetRawQuery());
    }

    public Task<List<CalendarEventAttendee>> GetAttendeesAsync(Guid calendarEventTrackingId)
        => Connection.Table<CalendarEventAttendee>().Where(x => x.CalendarItemId == calendarEventTrackingId).ToListAsync();

    public async Task<List<CalendarEventAttendee>> ManageEventAttendeesAsync(Guid calendarItemId, List<CalendarEventAttendee> allAttendees)
    {
        await Connection.RunInTransactionAsync((connection) =>
        {
            // Clear all attendees.
            var query = new Query()
                .From(nameof(CalendarEventAttendee))
                .Where(nameof(CalendarEventAttendee.CalendarItemId), calendarItemId)
                .AsDelete();

            connection.Execute(query.GetRawQuery());

            // Insert new attendees.
            connection.InsertAll(allAttendees);
        });

        return await Connection.Table<CalendarEventAttendee>().Where(a => a.CalendarItemId == calendarItemId).ToListAsync();
    }

    public async Task<CalendarItem> GetCalendarItemTargetAsync(CalendarItemTarget targetDetails)
    {
        var eventId = targetDetails.Item.Id;

        // Get the event by Id first.
        var item = await GetCalendarItemAsync(eventId).ConfigureAwait(false);

        bool isRecurringChild = targetDetails.Item.IsRecurringChild;
        bool isRecurringParent = targetDetails.Item.IsRecurringParent;

        if (targetDetails.TargetType == CalendarEventTargetType.Single)
        {
            if (isRecurringChild)
            {
                if (item == null)
                {
                    // This is an occurrence of a recurring event.
                    // They don't exist in db.

                    return targetDetails.Item;
                }
                else
                {
                    // Single exception occurrence of recurring event.
                    // Return the item.

                    return item;
                }
            }
            else if (isRecurringParent)
            {
                // Parent recurring events are never listed.
                Debugger.Break();
                return null;
            }
            else
            {
                // Single event.
                return item;
            }
        }
        else
        {
            // Series.

            if (isRecurringChild)
            {
                // Return the parent.
                return await GetCalendarItemAsync(targetDetails.Item.RecurringCalendarItemId.Value).ConfigureAwait(false);
            }
            else if (isRecurringParent)
                return item;
            else
            {
                // NA. Single events don't have series.
                Debugger.Break();
                return null;
            }
        }
    }
}
