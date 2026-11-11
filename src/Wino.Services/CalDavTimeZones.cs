#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Ical.Net.DataTypes;
using Serilog;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Services;

/// <summary>
/// Time zones of the events Wino writes. Other clients only know IANA TZIDs, so Windows ids are
/// converted, and every TZID an event uses gets a VTIMEZONE (RFC 5545 section 3.6.5).
/// </summary>
public static class CalDavTimeZones
{
    public const string UtcTzId = "UTC";

    private static readonly HashSet<string> UtcIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "UTC", "Etc/UTC", "Etc/UCT", "UCT", "Universal", "Etc/Universal", "Zulu", "Etc/Zulu", "GMT", "Etc/GMT", "Coordinated Universal Time"
    };

    public static bool IsUtc(string? timeZoneId)
        => !string.IsNullOrWhiteSpace(timeZoneId) &&
           (UtcIds.Contains(timeZoneId.Trim()) || string.Equals(timeZoneId.Trim(), TimeZoneInfo.Utc.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The TZID to write for a stored time zone id: <see cref="UtcTzId"/>, an IANA id, or null when
    /// the id is empty or unknown.
    /// </summary>
    public static string? ToIcsTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return null;

        var id = timeZoneId.Trim();
        if (IsUtc(id))
            return UtcTzId;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            return IsUtc(ianaId) ? UtcTzId : ianaId;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            return id;

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId ? zone.Id : null;
    }

    public static bool IsSameZone(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var leftId = ToIcsTimeZoneId(left);
        return leftId != null && string.Equals(leftId, ToIcsTimeZoneId(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds a VTIMEZONE for every TZID the events use that the calendar does not define.</summary>
    public static void AddMissingTimeZones(IcalCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var defined = new HashSet<string>(
            calendar.TimeZones.Select(zone => zone.TzId).OfType<string>().Where(tzId => tzId.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var earliestByTzId = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var calendarEvent in calendar.Events)
        {
            var values = new List<CalDateTime?> { calendarEvent.Start, calendarEvent.End, calendarEvent.RecurrenceIdentifier?.StartTime };
            values.AddRange(calendarEvent.ExceptionDates.GetAllDates());

            foreach (var value in values)
            {
                var tzId = value?.TzId;
                if (value == null || string.IsNullOrWhiteSpace(tzId) || IsUtc(tzId) || defined.Contains(tzId))
                    continue;

                if (!earliestByTzId.TryGetValue(tzId, out var earliest) || value.Value < earliest)
                    earliestByTzId[tzId] = value.Value;
            }
        }

        foreach (var (tzId, earliest) in earliestByTzId)
        {
            try
            {
                calendar.AddTimeZone(tzId, earliest, includeHistoricalData: false);
            }
            catch (Exception ex)
            {
                // A TZID without a definition is still readable by most servers; the event is written anyway.
                Log.ForContext(typeof(CalDavTimeZones)).Warning(ex, "Could not build a VTIMEZONE for {TzId}", tzId);
            }
        }
    }
}
