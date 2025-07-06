using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SqlKata;
using Wino.Core.Domain.Entities.Calendar;
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
        // TODO
        return new List<CalendarItem>();
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
        => Connection.Table<CalendarEventAttendee>().Where(x => x.EventId == calendarEventTrackingId).ToListAsync();

    public async Task<List<CalendarEventAttendee>> ManageEventAttendeesAsync(Guid calendarItemId, List<CalendarEventAttendee> allAttendees)
    {
        await Connection.RunInTransactionAsync((connection) =>
        {
            // Clear all attendees.
            var query = new Query()
                .From(nameof(CalendarEventAttendee))
                .Where(nameof(CalendarEventAttendee.EventId), calendarItemId)
                .AsDelete();

            connection.Execute(query.GetRawQuery());

            // Insert new attendees.
            connection.InsertAll(allAttendees);
        });

        return await Connection.Table<CalendarEventAttendee>().Where(a => a.EventId == calendarItemId).ToListAsync();
    }

    public async Task<CalendarItem> GetCalendarItemTargetAsync(CalendarItemTarget targetDetails)
    {
        // TODO
        return null;
    }
}
