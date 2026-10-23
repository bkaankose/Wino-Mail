#nullable enable
using System;
using System.Collections.Generic;

namespace MailCal.Core.Domain.Models.Calendar;

/// <summary>
/// One calendar object resource as a listing, a sync REPORT or a multiget describes it.
/// Listings carry only the href and ETag; downloads also carry the iCalendar text.
/// </summary>
public sealed class CalDavResource
{
    public string ExactHref { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
    public string IcsContent { get; init; } = string.Empty;

    /// <summary>The server reported the resource as gone (404 in a sync REPORT or multiget).</summary>
    public bool IsDeleted { get; init; }
}

/// <summary>One page of a sync-collection REPORT (RFC 6578).</summary>
public sealed class CalDavSyncPage
{
    public IReadOnlyList<CalDavResource> Changes { get; init; } = Array.Empty<CalDavResource>();

    /// <summary>
    /// The token that continues after this page. Empty when the response carried no
    /// DAV:sync-token, which Yahoo and Zimbra do when they answer an unsupported REPORT
    /// with an empty multistatus instead of an error.
    /// </summary>
    public string NextSyncToken { get; init; } = string.Empty;

    /// <summary>The server cut the result short (507 on the collection, RFC 6578 section 3.6).</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>
/// What a full CalDAV discovery learned about an account: the principal, its calendar homes and
/// scheduling addresses, the DAV compliance classes of the home, and every calendar collection
/// (including collections without VEVENT, so their component set can be recorded).
/// </summary>
public sealed class CalDavAccountDiscovery
{
    public string PrincipalHref { get; init; } = string.Empty;
    public IReadOnlyList<string> CalendarHomeHrefs { get; init; } = Array.Empty<string>();

    /// <summary>C:calendar-user-address-set without unusable values such as Yahoo's "mailto:null".</summary>
    public IReadOnlyList<string> CalendarUserAddresses { get; init; } = Array.Empty<string>();

    public string ScheduleInboxHref { get; init; } = string.Empty;
    public string ScheduleOutboxHref { get; init; } = string.Empty;

    /// <summary>Compliance classes from the DAV header of an OPTIONS request on the first calendar home.</summary>
    public IReadOnlyList<string> DavCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>Default alarms reported on the calendar home itself.</summary>
    public string DefaultAlarmVEventDateTime { get; init; } = string.Empty;
    public string DefaultAlarmVEventDate { get; init; } = string.Empty;

    public IReadOnlyList<CalDavCalendar> Calendars { get; init; } = Array.Empty<CalDavCalendar>();
}

/// <summary>
/// The stored copy of one calendar item's resource, read from its ICS metadata file.
/// Every row of a recurring series points at the same resource href.
/// </summary>
public sealed class CalendarIcsShadow
{
    public Guid CalendarItemId { get; init; }
    public string RemoteEventId { get; init; } = string.Empty;
    public string ExactHref { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
}
