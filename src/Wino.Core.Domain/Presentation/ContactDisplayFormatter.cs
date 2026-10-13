#nullable enable
using System;

namespace Wino.Core.Domain.Presentation;

/// <summary>Display text for contact card fields.</summary>
public static class ContactDisplayFormatter
{
    /// <summary>
    /// "14 March" or "14 March 1990" in the current culture; empty when the month or day is missing
    /// or out of range. A day past the month's end is clamped (29 February without a year shows as 29 February).
    /// </summary>
    public static string FormatBirthday(int? year, int? month, int? day)
    {
        if (month is not (>= 1 and <= 12) || day is not (>= 1 and <= 31)) return string.Empty;

        // A year DateTime cannot hold is treated as unknown rather than failing the whole card.
        var knownYear = year is >= 1 and <= 9999 ? year : null;
        var referenceYear = knownYear ?? 2000;
        var date = new DateTime(referenceYear, month.Value, Math.Min(day.Value, DateTime.DaysInMonth(referenceYear, month.Value)));

        return knownYear.HasValue ? date.ToString("d MMMM yyyy") : date.ToString("d MMMM");
    }
}
