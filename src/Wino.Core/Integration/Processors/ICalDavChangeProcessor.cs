using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Integration.Processors;

/// <summary>
/// Stores calendar events that come as iCalendar objects, with the ICS copy each one was read from. CalDAV, local
/// calendars and mail invitations write through it.
/// </summary>
public interface ICalDavChangeProcessor : IDefaultChangeProcessor
{
    Task ManageCalendarEventAsync(CalDavCalendarEvent calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount);
    Task SaveCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId, string remoteEventId, string remoteResourceHref, string eTag, string icsContent);
    Task<CalDavResourceSnapshot> GetCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId);
    Task<string> GetCalendarItemIcsETagAsync(Guid accountId, Guid calendarId, Guid calendarItemId);
    Task DeleteCalendarItemIcsAsync(Guid accountId, Guid calendarItemId);
    Task DeleteCalendarIcsForCalendarAsync(Guid accountId, Guid calendarId);
    Task<IReadOnlyList<CalendarIcsShadow>> GetCalendarIcsShadowsAsync(Guid accountId, Guid calendarId);
}
