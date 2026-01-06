using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
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

    /// <summary>
    /// Loads the AssignedCalendar (and its MailAccount) for a CalendarItem if not already loaded.
    /// </summary>
    private async Task LoadAssignedCalendarAsync(CalendarItem calendarItem)
    {
        if (calendarItem == null || calendarItem.AssignedCalendar != null) return;

        calendarItem.AssignedCalendar = await Connection.GetAsync<AccountCalendar>(calendarItem.CalendarId);
        if (calendarItem.AssignedCalendar != null)
        {
            calendarItem.AssignedCalendar.MailAccount = await Connection.GetAsync<MailAccount>(calendarItem.AssignedCalendar.AccountId);
        }
    }

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
    /// Returns all events (single instances and recurring event occurrences) that overlap with the period.
    /// Note: Recurring events are expected to be synced as individual instances from the server.
    /// Series master events (parents) are filtered out as they should not be displayed directly.
    /// </summary>
    /// <param name="calendar">The calendar to retrieve events from.</param>
    /// <param name="period">The time period to query events for.</param>
    /// <returns>List of calendar items that fall within the requested period.</returns>
    public async Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, ITimePeriod period)
    {
        // Fetch all non-hidden events for this calendar
        var accountEvents = await Connection.Table<CalendarItem>()
            .Where(x => x.CalendarId == calendar.Id && !x.IsHidden)
            .ToListAsync();

        var result = new List<CalendarItem>();

        foreach (var calendarItem in accountEvents)
        {
            // Skip series master events - they should not be displayed directly.
            // Individual instances are synced from the server and displayed instead.
            if (calendarItem.IsRecurringParent)
                continue;

            calendarItem.AssignedCalendar = calendar;

            // Check if the event overlaps with the requested period
            if (calendarItem.Period.OverlapsWith(period))
            {
                result.Add(calendarItem);
            }
        }

        return result;
    }

    public async Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId)
    {
        var calendar = await Connection.GetAsync<AccountCalendar>(accountCalendarId);
        if (calendar != null)
        {
            calendar.MailAccount = await Connection.GetAsync<MailAccount>(calendar.AccountId);
        }
        return calendar;
    }

    public async Task<CalendarItem> GetCalendarItemAsync(Guid id)
    {
        var calendarItem = await Connection.FindWithQueryAsync<CalendarItem>(
            "SELECT * FROM CalendarItem WHERE Id = ?",
            id);

        await LoadAssignedCalendarAsync(calendarItem);

        return calendarItem;
    }

    public async Task<CalendarItem> GetCalendarItemAsync(Guid accountCalendarId, string remoteEventId)
    {
        var calendarItem = await Connection.FindWithQueryAsync<CalendarItem>(
            "SELECT * FROM CalendarItem WHERE CalendarId = ? AND RemoteEventId = ?",
            accountCalendarId, remoteEventId);

        await LoadAssignedCalendarAsync(calendarItem);

        return calendarItem;
    }

    public Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken)
    {
        return Connection.ExecuteAsync(
            "UPDATE AccountCalendar SET SynchronizationDeltaToken = ? WHERE Id = ?",
            deltaToken, calendarId);
    }

    /// <summary>
    /// Gets attendees for a calendar item.
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

        // Get the event by Id first (this already loads AssignedCalendar).
        var item = await GetCalendarItemAsync(eventId).ConfigureAwait(false);

        bool isRecurringChild = targetDetails.Item.IsRecurringChild;
        bool isRecurringParent = targetDetails.Item.IsRecurringParent;

        if (targetDetails.TargetType == CalendarEventTargetType.Single)
        {
            if (isRecurringChild)
            {
                if (item == null)
                {
                    // This occurrence doesn't exist in db - return the passed item.
                    // Ensure AssignedCalendar is loaded.
                    await LoadAssignedCalendarAsync(targetDetails.Item);
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
    /// Gets reminders for a calendar item.
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

    #region Attachments

    public Task<List<CalendarAttachment>> GetAttachmentsAsync(Guid calendarItemId)
        => Connection.Table<CalendarAttachment>().Where(x => x.CalendarItemId == calendarItemId).ToListAsync();

    public async Task InsertOrReplaceAttachmentsAsync(List<CalendarAttachment> attachments)
    {
        if (attachments == null || attachments.Count == 0) return;

        foreach (var item in attachments)
        {
            // Check if an attachment with the same RemoteAttachmentId already exists for this calendar item
            // to avoid re-downloading already existing attachments.
            var existingAttachment = await Connection.Table<CalendarAttachment>()
                .FirstOrDefaultAsync(x => x.CalendarItemId == item.CalendarItemId
                                       && x.RemoteAttachmentId == item.RemoteAttachmentId);

            if (existingAttachment != null)
            {
                // Preserve the existing Id, IsDownloaded status, and LocalFilePath
                item.Id = existingAttachment.Id;
                item.IsDownloaded = existingAttachment.IsDownloaded;
                item.LocalFilePath = existingAttachment.LocalFilePath;
            }

            await Connection.InsertOrReplaceAsync(item, typeof(CalendarAttachment));
        }
    }

    public async Task MarkAttachmentDownloadedAsync(Guid attachmentId, string localFilePath)
    {
        var attachment = await Connection.Table<CalendarAttachment>().FirstOrDefaultAsync(x => x.Id == attachmentId);

        if (attachment == null) return;

        attachment.IsDownloaded = true;
        attachment.LocalFilePath = localFilePath;

        await Connection.UpdateAsync(attachment, typeof(CalendarAttachment));
    }

    public async Task DeleteAttachmentsAsync(Guid calendarItemId)
    {
        await Connection.ExecuteAsync("DELETE FROM CalendarAttachment WHERE CalendarItemId = ?", calendarItemId);
    }

    #endregion
}
