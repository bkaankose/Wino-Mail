using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Calendar;

public sealed class CalDavCalendar
{
    public string RemoteCalendarId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CTag { get; init; } = string.Empty;
    public string SyncToken { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public string BackgroundColorHex { get; init; } = string.Empty;
    public bool IsReadOnly { get; init; }
    public bool SupportsEvents { get; init; } = true;
    public CalendarItemShowAs DefaultShowAs { get; init; } = CalendarItemShowAs.Busy;
    public double? Order { get; init; }

    /// <summary>
    /// Component names from C:supported-calendar-component-set. Empty when the server did not
    /// report the property, which RFC 4791 section 5.2.3 defines as "every component".
    /// </summary>
    public IReadOnlyList<string> SupportedComponents { get; init; } = Array.Empty<string>();

    /// <summary>REPORT names from DAV:supported-report-set, as "{namespace}local-name".</summary>
    public IReadOnlyList<string> SupportedReports { get; init; } = Array.Empty<string>();

    /// <summary>True when the collection advertises (or, without a report set, implies) sync-collection (RFC 6578).</summary>
    public bool SupportsSyncCollection { get; init; }

    /// <summary>True when the collection advertises calendar-multiget, or reports no report set at all.</summary>
    public bool SupportsMultiget { get; init; } = true;

    public long? QuotaAvailableBytes { get; init; }
    public long? QuotaUsedBytes { get; init; }

    /// <summary>C:max-resource-size as reported. Yahoo reports 0, meaning no limit.</summary>
    public long? MaxResourceSize { get; init; }

    public int? MaxAttendeesPerInstance { get; init; }

    /// <summary>Raw VALARM text of C:default-alarm-vevent-datetime (draft-daboo-valarm-extensions).</summary>
    public string DefaultAlarmVEventDateTime { get; init; } = string.Empty;

    /// <summary>Raw VALARM text of C:default-alarm-vevent-date.</summary>
    public string DefaultAlarmVEventDate { get; init; } = string.Empty;

    public string OwnerHref { get; init; } = string.Empty;
}

