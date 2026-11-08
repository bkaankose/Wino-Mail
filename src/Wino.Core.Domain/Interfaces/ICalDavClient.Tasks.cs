#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

/// <summary>VTODO listings and calendar collection management for CalDAV task lists.</summary>
public partial interface ICalDavClient
{
    /// <summary>Lists the href and ETag of every VTODO resource of the collection, without calendar data.</summary>
    Task<IReadOnlyList<CalDavResource>> ListTaskResourcesAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a calendar collection with MKCALENDAR (RFC 4791 section 5.3.1) under the calendar
    /// home and returns its remote calendar id. Throws <see cref="System.NotSupportedException"/>
    /// when the server does not allow creating collections.
    /// </summary>
    Task<string> CreateCalendarCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        string calendarHomeHref,
        string collectionName,
        string displayName,
        IReadOnlyList<string> components,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a collection with PROPPATCH and, when <paramref name="colorHex"/> is set, recolors it.
    /// A server that refuses only the color keeps the new name.
    /// </summary>
    Task UpdateCalendarCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        string remoteCalendarId,
        string displayName,
        string? colorHex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection. A collection that is already gone counts as deleted. Throws
    /// <see cref="System.NotSupportedException"/> when the server does not allow deleting it.
    /// </summary>
    Task DeleteCalendarCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        string remoteCalendarId,
        CancellationToken cancellationToken = default);
}
