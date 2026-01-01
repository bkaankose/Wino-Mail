using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Itenso.TimePeriod;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Services;

public class CalendarService : BaseDatabaseService, ICalendarService
{
    // Predefined reminder options in minutes
    private static readonly int[] PredefinedReminderMinutes = [60, 30, 15, 5, 1];

    public CalendarService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public int[] GetPredefinedReminderMinutes() => PredefinedReminderMinutes;

    public Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId)
        => Connection.Table<AccountCalendar>().Where(x => x.AccountId == accountId).OrderByDescending(a => a.IsPrimary).ToListAsync();

    public async Task InsertAccountCalendarAsync(AccountCalendar accountCalendar)
    {
        await Connection.InsertAsync(accountCalendar, typeof(AccountCalendar));

        WeakReferenceMessenger.Default.Send(new CalendarListAdded(accountCalendar));
    }

    public async Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar)
    {
        await Connection.UpdateAsync(accountCalendar, typeof(AccountCalendar));

        WeakReferenceMessenger.Default.Send(new CalendarListUpdated(accountCalendar));
    }

    public async Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar)
    {
        await Connection.ExecuteAsync(
            "DELETE FROM CalendarItem WHERE CalendarId = ? AND AccountId = ?",
            accountCalendar.Id, accountCalendar.AccountId);
        await Connection.DeleteAsync<AccountCalendar>(accountCalendar.Id);

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

    public async Task DeleteCalendarItemAsync(string calendarRemoteEventId, Guid calendarId)
    {
        var calendarItem = await Connection.FindWithQueryAsync<CalendarItem>(
            "SELECT * FROM CalendarItem WHERE CalendarId = ? AND RemoteEventId = ?",
            calendarId, calendarRemoteEventId);

        if (calendarItem == null) return;

        await DeleteCalendarItemAsync(calendarItem.Id);
    }

    public async Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees)
    {
        try
        {
            await Connection.RunInTransactionAsync((conn) =>
            {
                conn.Insert(calendarItem, typeof(CalendarItem));

                if (attendees != null)
                {
                    conn.InsertAll(attendees, typeof(CalendarEventAttendee));
                }
            });

            WeakReferenceMessenger.Default.Send(new CalendarItemAdded(calendarItem));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating new calendar item");
        }
    }

    public async Task UpdateCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees)
    {
        try
        {
            await Connection.RunInTransactionAsync((conn) =>
            {
                conn.Update(calendarItem, typeof(CalendarItem));

                // Clear existing attendees and add new ones
                conn.Table<CalendarEventAttendee>().Delete(a => a.CalendarItemId == calendarItem.Id);

                if (attendees != null)
                {
                    conn.InsertAll(attendees, typeof(CalendarEventAttendee));
                }
            });

            WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(calendarItem));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating calendar item");
        }
    }

    /// <summary>
    /// Retrieves calendar events for a given calendar within the specified time period.
    /// This includes regular events and expanded recurring event occurrences based on RFC 5545 patterns.
    /// </summary>
    /// <param name="calendar">The calendar to retrieve events from.</param>
    /// <param name="period">The time period to query events for.</param>
    /// <returns>List of calendar items including regular events and recurring event occurrences.</returns>
    public async Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, ITimePeriod period)
    {
        // TODO: Implement caching strategy for better performance with large event sets.
        // Consider using a cache keyed by calendar ID and time period.

        var accountEvents = await Connection.Table<CalendarItem>()
            .Where(x => x.CalendarId == calendar.Id && !x.IsHidden)
            .ToListAsync();

        var result = new List<CalendarItem>();

        foreach (var calendarItem in accountEvents)
        {
            calendarItem.AssignedCalendar = calendar;

            // Skip exception instances - they will be handled by their parent recurring event
            if (calendarItem.RecurringCalendarItemId.HasValue)
            {
                continue;
            }

            if (string.IsNullOrEmpty(calendarItem.Recurrence))
            {
                // Regular non-recurring event - simply check if it overlaps with the requested period.
                if (calendarItem.Period.OverlapsWith(period))
                {
                    result.Add(calendarItem);
                }
            }
            else
            {
                // Recurring event - expand occurrences within the period.
                // Wino stores recurring events as a series master with RFC 5545 recurrence rules.
                // Exception instances (modified or cancelled) are stored separately and linked via RecurringCalendarItemId.
                var expandedOccurrences = await ExpandRecurringEventAsync(calendarItem, period);
                result.AddRange(expandedOccurrences);
            }
        }

        return result;
    }

    /// <summary>
    /// Expands a recurring event into its occurrences within the specified period.
    /// Handles exception instances (modified or cancelled occurrences) by excluding them from the expansion.
    /// </summary>
    /// <param name="recurringEvent">The recurring event series master.</param>
    /// <param name="period">The time period to expand occurrences within.</param>
    /// <returns>List of calendar items representing individual occurrences in the period.</returns>
    private async Task<List<CalendarItem>> ExpandRecurringEventAsync(CalendarItem recurringEvent, ITimePeriod period)
    {
        var result = new List<CalendarItem>();

        // Parse the RFC 5545 recurrence pattern.
        var calendarEvent = new CalendarEvent
        {
            Start = new CalDateTime(recurringEvent.StartDate),
            End = new CalDateTime(recurringEvent.EndDate),
        };

        var recurrenceLines = Regex.Split(recurringEvent.Recurrence, Constants.CalendarEventRecurrenceRuleSeperator);
        foreach (var line in recurrenceLines)
        {
            calendarEvent.RecurrenceRules.Add(new RecurrencePattern(line));
        }

        // Calculate all occurrences in the requested period using iCal.NET.
        var occurrences = calendarEvent.GetOccurrences(period.Start, period.End);

        // Retrieve exception instances (modified or cancelled occurrences).
        // These are stored as separate CalendarItem records with RecurringCalendarItemId set.
        var exceptionInstances = await Connection.Table<CalendarItem>()
            .Where(a => a.RecurringCalendarItemId == recurringEvent.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var occurrence in occurrences)
        {
            // Check if this occurrence has been modified/cancelled (exception instance exists).
            // Compare by checking if an exception instance overlaps with this occurrence's time window.
            var occurrenceStart = occurrence.Period.StartTime.Value;
            var occurrenceEnd = occurrence.Period.EndTime?.Value ?? occurrenceStart.Add(occurrence.Period.Duration);

            var exceptionInstance = exceptionInstances.FirstOrDefault(a =>
                a.StartDate <= occurrenceEnd && a.EndDate >= occurrenceStart);

            if (exceptionInstance == null)
            {
                // No exception - create a virtual occurrence from the series master.
                var occurrenceItem = recurringEvent.CreateRecurrence(
                    occurrenceStart,
                    occurrence.Period.Duration.TotalSeconds);

                result.Add(occurrenceItem);
            }
            else if (!exceptionInstance.IsHidden && exceptionInstance.Period.OverlapsWith(period))
            {
                // Exception exists and is not hidden - include the modified version.
                exceptionInstance.AssignedCalendar = recurringEvent.AssignedCalendar;
                result.Add(exceptionInstance);
            }
            // If exception is hidden, skip this occurrence entirely.
        }

        return result;
    }

    public Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId)
        => Connection.GetAsync<AccountCalendar>(accountCalendarId);

    public Task<CalendarItem> GetCalendarItemAsync(Guid id)
    {
        return Connection.FindWithQueryAsync<CalendarItem>(
            "SELECT * FROM CalendarItem WHERE Id = ?",
            id);
    }

    public async Task<CalendarItem> GetCalendarItemAsync(Guid accountCalendarId, string remoteEventId)
    {
        var calendarItem = await Connection.FindWithQueryAsync<CalendarItem>(
            "SELECT * FROM CalendarItem WHERE CalendarId = ? AND RemoteEventId = ?",
            accountCalendarId, remoteEventId);

        // Load assigned calendar.
        if (calendarItem != null)
        {
            calendarItem.AssignedCalendar = await Connection.GetAsync<AccountCalendar>(calendarItem.CalendarId);
        }

        return calendarItem;
    }

    public Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken)
    {
        return Connection.ExecuteAsync(
            "UPDATE AccountCalendar SET SynchronizationDeltaToken = ? WHERE Id = ?",
            deltaToken, calendarId);
    }

    /// <summary>
    /// Expands a recurring calendar item to check if any of its occurrences fall within the given periods.
    /// For non-recurring events, returns the item if it overlaps with any period.
    /// For recurring events, expands occurrences and returns those that fall within any of the periods.
    /// </summary>
    /// <param name="calendarItem">The calendar item to expand (can be recurring or non-recurring).</param>
    /// <param name="periods">The list of periods to check against.</param>
    /// <returns>List of calendar items (either the original item or expanded recurrence instances) that fall within the periods.</returns>
    public async Task<List<CalendarItem>> GetExpandedRecurringEventsForPeriodsAsync(CalendarItem calendarItem, IEnumerable<ITimePeriod> periods)
    {
        var result = new List<CalendarItem>();

        if (calendarItem == null || periods == null || !periods.Any())
        {
            return result;
        }

        // Ensure AssignedCalendar is loaded
        if (calendarItem.AssignedCalendar == null)
        {
            calendarItem.AssignedCalendar = await GetAccountCalendarAsync(calendarItem.CalendarId);
        }

        // For non-recurring events, check if it overlaps with any of the provided periods
        if (string.IsNullOrEmpty(calendarItem.Recurrence))
        {
            foreach (var period in periods)
            {
                if (calendarItem.Period.OverlapsWith(period))
                {
                    result.Add(calendarItem);
                    break; // Add it only once
                }
            }
        }
        else
        {
            // For recurring events, expand occurrences for the combined date range of all periods
            // Find the minimum and maximum dates across all periods
            var minDate = periods.Min(p => p.Start);
            var maxDate = periods.Max(p => p.End);

            var combinedPeriod = new TimeRange(minDate, maxDate);

            // Expand the recurring event for the combined period
            var expandedOccurrences = await ExpandRecurringEventAsync(calendarItem, combinedPeriod);

            // Filter occurrences that fall within any of the individual periods
            foreach (var occurrence in expandedOccurrences)
            {
                foreach (var period in periods)
                {
                    if (occurrence.Period.OverlapsWith(period))
                    {
                        result.Add(occurrence);
                        break; // Add it only once even if it overlaps with multiple periods
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets attendees for a calendar item. For recurring event occurrences,
    /// callers should pass the EventTrackingId which returns the parent's ID.
    /// </summary>
    public Task<List<CalendarEventAttendee>> GetAttendeesAsync(Guid calendarEventTrackingId)
        => Connection.Table<CalendarEventAttendee>().Where(x => x.CalendarItemId == calendarEventTrackingId).ToListAsync();

    public async Task<List<CalendarEventAttendee>> ManageEventAttendeesAsync(Guid calendarItemId, List<CalendarEventAttendee> allAttendees)
    {
        await Connection.RunInTransactionAsync((connection) =>
        {
            // Clear all attendees.
            connection.Execute(
                "DELETE FROM CalendarEventAttendee WHERE CalendarItemId = ?",
                calendarItemId);

            // Insert new attendees.
            connection.InsertAll(allAttendees, typeof(CalendarEventAttendee));
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

    /// <summary>
    /// Gets reminders for a calendar item. For recurring event occurrences,
    /// callers should pass the EventTrackingId which returns the parent's ID.
    /// </summary>
    public Task<List<Reminder>> GetRemindersAsync(Guid calendarItemId)
        => Connection.Table<Reminder>().Where(r => r.CalendarItemId == calendarItemId).ToListAsync();

    public async Task SaveRemindersAsync(Guid calendarItemId, List<Reminder> reminders)
    {
        await Connection.RunInTransactionAsync((connection) =>
        {
            // Clear existing reminders for this calendar item
            connection.Execute(
                "DELETE FROM Reminder WHERE CalendarItemId = ?",
                calendarItemId);

            // Insert new reminders if any
            if (reminders != null && reminders.Count > 0)
            {
                connection.InsertAll(reminders, typeof(Reminder));
            }
        });
    }
}
