using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Persists CalDAV ICS payloads on disk for IMAP accounts.
/// </summary>
public interface ICalendarIcsFileService
{
    Task SaveCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId, string remoteEventId, string remoteResourceHref, string eTag, string icsContent);
    Task<CalDavResourceSnapshot> GetCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId);
    Task<string> GetCalendarItemIcsETagAsync(Guid accountId, Guid calendarId, Guid calendarItemId);
    Task DeleteCalendarItemIcsAsync(Guid accountId, Guid calendarItemId);
    Task DeleteCalendarIcsForCalendarAsync(Guid accountId, Guid calendarId);

    /// <summary>
    /// Lists the stored resource metadata (item, href, ETag, remote event ID) of every item of a
    /// calendar. Reads only the small metadata files, not the ICS payloads.
    /// </summary>
    Task<IReadOnlyList<CalendarIcsShadow>> GetCalendarIcsShadowsAsync(Guid accountId, Guid calendarId);
}
