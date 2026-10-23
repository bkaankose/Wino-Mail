#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MailCal.Core.Domain.Models.Calendar;

/// <summary>
/// CalDAV knowledge about one account that the database does not hold: discovery results,
/// server capabilities and per-calendar sync checkpoints. It is a rebuildable cache stored as
/// JSON beside the account's ICS files; losing it only costs a rediscovery and one listing.
/// It never holds credentials.
/// </summary>
public sealed class CalDavAccountState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string PrincipalHref { get; set; } = string.Empty;
    public List<string> CalendarHomeHrefs { get; set; } = [];
    public List<string> CalendarUserAddresses { get; set; } = [];
    public string ScheduleInboxHref { get; set; } = string.Empty;
    public string ScheduleOutboxHref { get; set; } = string.Empty;

    /// <summary>Compliance classes from the DAV header, for example "calendar-auto-schedule".</summary>
    public List<string> DavCapabilities { get; set; } = [];

    public string DefaultAlarmVEventDateTime { get; set; } = string.Empty;
    public string DefaultAlarmVEventDate { get; set; } = string.Empty;
    public DateTime? DiscoveredAtUtc { get; set; }

    /// <summary>Calendars keyed by <see cref="CalDavCalendarState.RemoteCalendarId"/>.</summary>
    public Dictionary<string, CalDavCalendarState> Calendars { get; set; } = [];

    [JsonIgnore]
    public bool HasDiscovery => DiscoveredAtUtc.HasValue && CalendarHomeHrefs.Count > 0;

    public bool HasDavCapability(string capability)
        => DavCapabilities.Any(value => string.Equals(value, capability, StringComparison.OrdinalIgnoreCase));

    /// <summary>Remote calendar IDs are compared without case elsewhere in Mail-Cal, so lookups are too.</summary>
    public CalDavCalendarState? FindCalendar(string? remoteCalendarId)
    {
        if (string.IsNullOrWhiteSpace(remoteCalendarId))
            return null;

        if (Calendars.TryGetValue(remoteCalendarId, out var exact))
            return exact;

        return Calendars.Values.FirstOrDefault(calendar =>
            string.Equals(calendar.RemoteCalendarId, remoteCalendarId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class CalDavCalendarState
{
    public string RemoteCalendarId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> SupportedComponents { get; set; } = [];
    public List<string> SupportedReports { get; set; } = [];
    public bool SupportsSyncCollection { get; set; }
    public bool SupportsMultiget { get; set; } = true;

    /// <summary>The server advertised sync-collection but refused it or answered without a token.</summary>
    public bool SyncCollectionRefused { get; set; }

    /// <summary>The server refused calendar-multiget; resources are fetched with GET.</summary>
    public bool MultigetRefused { get; set; }

    public bool IsReadOnly { get; set; }
    public long? QuotaAvailableBytes { get; set; }
    public long? QuotaUsedBytes { get; set; }
    public long? MaxResourceSize { get; set; }
    public int? MaxAttendeesPerInstance { get; set; }
    public string DefaultAlarmVEventDateTime { get; set; } = string.Empty;
    public string DefaultAlarmVEventDate { get; set; } = string.Empty;
    public string OwnerHref { get; set; } = string.Empty;

    /// <summary>The token a later sync-collection REPORT continues from.</summary>
    public string LastSyncToken { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="LastSyncToken"/> came from a sync-collection REPORT. A token taken from
    /// PROPFIND that the server then refuses means the server does not accept its own property
    /// value, so sync-collection is not tried again.
    /// </summary>
    public bool LastSyncTokenFromReport { get; set; }

    public string LastCTag { get; set; } = string.Empty;
    public DateTime? LastSuccessfulSyncUtc { get; set; }

    [JsonIgnore]
    public bool SupportsEvents => SupportedComponents.Count == 0 ||
                                  SupportedComponents.Contains("VEVENT", StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool CanUseSyncCollection => SupportsSyncCollection && !SyncCollectionRefused;

    [JsonIgnore]
    public bool CanUseMultiget => SupportsMultiget && !MultigetRefused;
}
