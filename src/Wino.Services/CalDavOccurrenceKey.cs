using System;
using System.Globalization;
using Ical.Net.DataTypes;

namespace Wino.Services;

/// <summary>
/// Builds the occurrence part of a CalDAV remote event ID ("UID::key").
/// Keys are always expressed in the value type of the series master so that a
/// RECURRENCE-ID written as floating, zoned, or UTC matches the generated occurrence
/// even when the writing client used a different value type than DTSTART.
/// </summary>
public static class CalDavOccurrenceKey
{
    private const string FloatingFormat = "yyyyMMdd'T'HHmmss";
    private const string UtcFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static string Create(CalDateTime value)
    {
        if (value == null)
            return string.Empty;

        return value.IsFloating
            ? value.Value.ToString(FloatingFormat, CultureInfo.InvariantCulture)
            : value.AsUtc.ToString(UtcFormat, CultureInfo.InvariantCulture);
    }

    public static string Create(CalDateTime value, CalDateTime masterStart)
    {
        if (value == null)
            return string.Empty;

        if (masterStart == null || value.IsFloating == masterStart.IsFloating)
            return Create(value);

        if (masterStart.IsFloating)
        {
            // Floating series, absolute RECURRENCE-ID: the wall clock of the exception is the best match.
            return value.Value.ToString(FloatingFormat, CultureInfo.InvariantCulture);
        }

        // Absolute series, floating RECURRENCE-ID: interpret the wall clock in the series time zone.
        var zoned = new CalDateTime(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), masterStart.TzId, value.HasTime);
        return zoned.AsUtc.ToString(UtcFormat, CultureInfo.InvariantCulture);
    }
}
