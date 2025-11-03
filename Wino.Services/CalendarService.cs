using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.EntityFrameworkCore;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Services;

public class CalendarService : BaseDatabaseService, ICalendarService
{
    public CalendarService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public async Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.AccountCalendars
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(a => a.IsPrimary)
            .ToListAsync();
    }

    public async Task InsertAccountCalendarAsync(AccountCalendar accountCalendar)
    {
        using var context = ContextFactory.CreateDbContext();
        context.AccountCalendars.Add(accountCalendar);
        await context.SaveChangesAsync();

        WeakReferenceMessenger.Default.Send(new CalendarListAdded(accountCalendar));
    }

    public async Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar)
    {
        using var context = ContextFactory.CreateDbContext();
        context.AccountCalendars.Update(accountCalendar);
        await context.SaveChangesAsync();

        WeakReferenceMessenger.Default.Send(new CalendarListUpdated(accountCalendar));
    }

    public async Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar)
    {
        using var context = ContextFactory.CreateDbContext();
        
        // Delete all calendar items for this calendar first
        await context.CalendarItems
            .Where(x => x.CalendarId == accountCalendar.Id)
            .ExecuteDeleteAsync();

        context.AccountCalendars.Remove(accountCalendar);
        await context.SaveChangesAsync();

        WeakReferenceMessenger.Default.Send(new CalendarListDeleted(accountCalendar));
    }

    public async Task DeleteCalendarItemAsync(Guid calendarItemId)
    {
        using var context = ContextFactory.CreateDbContext();
        
        var calendarItem = await context.CalendarItems.FirstOrDefaultAsync(x => x.Id == calendarItemId);

        if (calendarItem == null) return;

        List<CalendarItem> eventsToRemove = new() { calendarItem };

        // In case of parent event, delete all child events as well.
        if (!string.IsNullOrEmpty(calendarItem.Recurrence))
        {
            var recurringEvents = await context.CalendarItems
                .Where(a => a.RecurringCalendarItemId == calendarItemId)
                .ToListAsync()
                .ConfigureAwait(false);

            eventsToRemove.AddRange(recurringEvents);
        }

        foreach (var @event in eventsToRemove)
        {
            await context.CalendarItems
                .Where(x => x.Id == @event.Id)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);
                
            await context.CalendarEventAttendees
                .Where(a => a.CalendarItemId == @event.Id)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(@event));
        }
    }

    public async Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees)
    {
        using var context = ContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        
        try
        {
            context.CalendarItems.Add(calendarItem);
            await context.SaveChangesAsync();

            if (attendees != null)
            {
                context.CalendarEventAttendees.AddRange(attendees);
                await context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            WeakReferenceMessenger.Default.Send(new CalendarItemAdded(calendarItem));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, DayRangeRenderModel dayRangeRenderModel)
    {
        // TODO: We might need to implement caching here.
        // I don't know how much of the events we'll have in total, but this logic scans all events every time for given calendar.

        using var context = ContextFactory.CreateDbContext();
        
        var accountEvents = await context.CalendarItems
            .Where(x => x.CalendarId == calendar.Id && !x.IsHidden)
            .ToListAsync();

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
                var exceptionalRecurrences = await context.CalendarItems
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

    public async Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.AccountCalendars.FirstOrDefaultAsync(x => x.Id == accountCalendarId);
    }

    public async Task<CalendarItem> GetCalendarItemAsync(Guid id)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.CalendarItems.FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<CalendarItem> GetCalendarItemAsync(Guid accountCalendarId, string remoteEventId)
    {
        using var context = ContextFactory.CreateDbContext();
        
        var calendarItem = await context.CalendarItems
            .FirstOrDefaultAsync(x => x.CalendarId == accountCalendarId && x.RemoteEventId == remoteEventId);

        // Load assigned calendar.
        if (calendarItem != null)
        {
            calendarItem.AssignedCalendar = await context.AccountCalendars
                .FirstOrDefaultAsync(x => x.Id == calendarItem.CalendarId);
        }

        return calendarItem;
    }

    public async Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken)
    {
        using var context = ContextFactory.CreateDbContext();
        
        await context.AccountCalendars
            .Where(x => x.Id == calendarId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.SynchronizationDeltaToken, deltaToken));
    }

    public async Task<List<CalendarEventAttendee>> GetAttendeesAsync(Guid calendarEventTrackingId)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.CalendarEventAttendees
            .Where(x => x.CalendarItemId == calendarEventTrackingId)
            .ToListAsync();
    }

    public async Task<List<CalendarEventAttendee>> ManageEventAttendeesAsync(Guid calendarItemId, List<CalendarEventAttendee> allAttendees)
    {
        using var context = ContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        
        try
        {
            // Clear all attendees.
            await context.CalendarEventAttendees
                .Where(x => x.CalendarItemId == calendarItemId)
                .ExecuteDeleteAsync();

            // Insert new attendees.
            if (allAttendees?.Count > 0)
            {
                context.CalendarEventAttendees.AddRange(allAttendees);
                await context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            
            return await context.CalendarEventAttendees
                .Where(a => a.CalendarItemId == calendarItemId)
                .ToListAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
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
