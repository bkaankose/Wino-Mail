using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Persists CalDAV ICS payloads on disk for IMAP accounts.
/// </summary>
public interface ICalendarIcsFileService
{
    Task SaveCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId, string remoteEventId, string remoteResourceHref, string eTag, string icsContent);
    Task DeleteCalendarItemIcsAsync(Guid accountId, Guid calendarItemId);
    Task DeleteCalendarIcsForCalendarAsync(Guid accountId, Guid calendarId);
}
