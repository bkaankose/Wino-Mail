using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// CalDAV protocol requests. The interface is split by feature into partial files
/// (ICalDavClient.Sync.cs, ...) so features can be added without touching each other.
/// </summary>
public partial interface ICalDavClient
{
    /// <summary>
    /// Confirms the service accepts the sign-in by resolving the principal and its calendar home. No calendar, event or
    /// task list is read. Throws what a refused or failed request throws.
    /// </summary>
    Task VerifyAccessAsync(
        CalDavConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalDavCalendar>> DiscoverCalendarsAsync(
        CalDavConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalDavCalendarEvent>> GetCalendarEventsAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    Task<CalDavWriteResult> UpsertCalendarEventAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        CalDavWriteRequest writeRequest,
        CancellationToken cancellationToken = default);

    Task DeleteCalendarEventAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        string eTag,
        CancellationToken cancellationToken = default);
}

