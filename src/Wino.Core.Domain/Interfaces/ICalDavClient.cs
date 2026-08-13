using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

public interface ICalDavClient
{
    Task<IReadOnlyList<CalDavCalendar>> DiscoverCalendarsAsync(
        CalDavConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalDavCalendarEvent>> GetCalendarEventsAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    Task UpsertCalendarEventAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        string remoteEventId,
        string icsContent,
        CancellationToken cancellationToken = default);

    Task DeleteCalendarEventAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        string remoteEventId,
        CancellationToken cancellationToken = default);
}

