using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// Moves a calendar row to the folder id another Exchange transport uses for the same server folder. The row keeps its
/// id and settings (colour, synchronization, primary); its events are removed with their attendees, reminders and
/// attachments, because their ids belong to the previous transport, and the new transport's first pass stores them
/// again.
/// </summary>
public static class ExchangeCalendarRekey
{
    public static async Task RekeyAsync(SQLiteAsyncConnection connection, ICalendarService calendars, AccountCalendar calendar, string remoteCalendarId)
    {
        // Series masters first: deleting one removes its occurrences and exceptions.
        foreach (var id in await connection.QueryScalarsAsync<string>(
                     "SELECT Id FROM CalendarItem WHERE CalendarId = ? AND Recurrence IS NOT NULL AND Recurrence <> ''", calendar.Id).ConfigureAwait(false))
            await DeleteIfPresentAsync(connection, calendars, id).ConfigureAwait(false);

        foreach (var id in await connection.QueryScalarsAsync<string>("SELECT Id FROM CalendarItem WHERE CalendarId = ?", calendar.Id).ConfigureAwait(false))
            await DeleteIfPresentAsync(connection, calendars, id).ConfigureAwait(false);

        calendar.RemoteCalendarId = remoteCalendarId;
        calendar.SynchronizationDeltaToken = null;
        await calendars.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
    }

    private static async Task DeleteIfPresentAsync(SQLiteAsyncConnection connection, ICalendarService calendars, string id)
    {
        if (!System.Guid.TryParse(id, out var itemId))
            return;

        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CalendarItem WHERE Id = ?", itemId).ConfigureAwait(false) > 0)
            await calendars.DeleteCalendarItemAsync(itemId).ConfigureAwait(false);
    }
}
