using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

public interface ICalendarService
{
    Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId);
    Task<AccountCalendar> GetAccountCalendarAsync(Guid accountCalendarId);
    Task DeleteCalendarItemAsync(Guid calendarItemId);
    Task DeleteCalendarItemAsync(string calendarRemoteEventId, Guid calendarId);

    Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar);
    Task InsertAccountCalendarAsync(AccountCalendar accountCalendar);
    Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar);
    Task CreateNewCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees);
    
    /// <summary>
    /// Retrieves calendar events for a given calendar within the specified time period.
    /// </summary>
    /// <param name="calendar">The calendar to retrieve events from.</param>
    /// <param name="period">The time period to query events for.</param>
    /// <returns>List of calendar items that fall within the requested period.</returns>
    Task<List<CalendarItem>> GetCalendarEventsAsync(IAccountCalendar calendar, ITimePeriod period);
    
    Task<CalendarItem> GetCalendarItemAsync(Guid accountCalendarId, string remoteEventId);
    Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken);

    /// <summary>
    /// Returns the correct calendar item based on the target details.
    /// </summary>
    /// <param name="targetDetails">Target details.</param>
    Task<CalendarItem> GetCalendarItemTargetAsync(CalendarItemTarget targetDetails);
    Task<CalendarItem> GetCalendarItemAsync(Guid id);
    Task<List<CalendarEventAttendee>> GetAttendeesAsync(Guid calendarEventTrackingId);
    Task<List<CalendarEventAttendee>> ManageEventAttendeesAsync(Guid calendarItemId, List<CalendarEventAttendee> allAttendees);
    Task UpdateCalendarItemAsync(CalendarItem calendarItem, List<CalendarEventAttendee> attendees);
    Task<List<Reminder>> GetRemindersAsync(Guid calendarItemId);
    Task SaveRemindersAsync(Guid calendarItemId, List<Reminder> reminders);

    /// <summary>
    /// Gets predefined reminder options in minutes (1 Hour, 30 Min, 15 Min, 5 Min, 1 Min).
    /// </summary>
    int[] GetPredefinedReminderMinutes();

    #region Attachments

    /// <summary>
    /// Gets all attachments for a calendar event.
    /// </summary>
    Task<List<CalendarAttachment>> GetAttachmentsAsync(Guid calendarItemId);

    /// <summary>
    /// Inserts or updates calendar attachments.
    /// </summary>
    Task InsertOrReplaceAttachmentsAsync(List<CalendarAttachment> attachments);

    /// <summary>
    /// Marks an attachment as downloaded and updates its local file path.
    /// </summary>
    Task MarkAttachmentDownloadedAsync(Guid attachmentId, string localFilePath);

    /// <summary>
    /// Deletes all attachments for a calendar item.
    /// </summary>
    Task DeleteAttachmentsAsync(Guid calendarItemId);

    #endregion
}
