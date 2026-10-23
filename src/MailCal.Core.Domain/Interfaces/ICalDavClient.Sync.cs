#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>Discovery with capabilities, and the requests behind incremental synchronization.</summary>
public partial interface ICalDavClient
{
    /// <summary>
    /// Resolves the principal, its calendar homes and scheduling addresses, reads the DAV
    /// header of the first home and lists every calendar collection with its capabilities.
    /// </summary>
    Task<CalDavAccountDiscovery> DiscoverAccountAsync(
        CalDavConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every calendar collection (any component set) of already known calendar homes.</summary>
    Task<IReadOnlyList<CalDavCalendar>> ListCalendarsAsync(
        CalDavConnectionSettings connectionSettings,
        IReadOnlyList<string> calendarHomeHrefs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One sync-collection REPORT asking for ETags only. An empty <paramref name="syncToken"/>
    /// lists the whole collection.
    /// </summary>
    Task<CalDavSyncPage> SyncCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        string? syncToken,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the href and ETag of every VEVENT resource overlapping the window, without calendar data.</summary>
    Task<IReadOnlyList<CalDavResource>> ListEventResourcesAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the given resources with one calendar-multiget REPORT. Resources the server
    /// did not return are missing from the result; resources it reported as gone are marked deleted.
    /// </summary>
    Task<IReadOnlyList<CalDavResource>> MultiGetEventsAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads one resource with GET. A 404 is returned as a deleted resource.</summary>
    Task<CalDavResource> GetEventResourceAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Expands one iCalendar resource into the events and occurrences that overlap the window.
    /// Series masters come first. Makes no request.
    /// </summary>
    IReadOnlyList<CalDavCalendarEvent> ExpandCalendarResource(
        string icsContent,
        string resourceHref,
        string eTag,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc);
}
