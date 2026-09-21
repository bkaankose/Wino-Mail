using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Maps the iCalendar RRULE the app stores on <see cref="CalendarItem.Recurrence"/> to the EWS
/// recurrence of a series master. The shapes are the ones the event composer and the other
/// providers produce: daily, weekly on days, monthly and yearly by date or by the nth weekday,
/// ending never, after a count or on a date. The MAPI transport encodes the same rule through
/// <c>RecurrenceEncoder</c>; both read it the same way.
/// </summary>
public static class EwsRecurrenceMapper
{
    private static readonly string[] DayTokens = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>The RRULE line of the item's recurrence text, or null when it has none.</summary>
    public static string GetRecurrenceRule(CalendarItem item)
        => item?.Recurrence?
            .Split(Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The EWS recurrence for a rule whose first occurrence starts on <paramref name="firstStart"/>
    /// (wall clock in the appointment's time zone), or null when the rule is absent or has a
    /// frequency Exchange cannot hold.
    /// </summary>
    public static Recurrence Create(string recurrenceRule, DateTime firstStart)
    {
        if (string.IsNullOrWhiteSpace(recurrenceRule))
            return null;

        var text = recurrenceRule.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            text = text["RRULE:".Length..];

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries)))
        {
            if (pair.Length == 2)
                parts[pair[0]] = pair[1];
        }

        if (!parts.TryGetValue("FREQ", out var frequency))
            return null;

        var startDate = firstStart.Date;
        var interval = Math.Max(1, ParseInt(parts, "INTERVAL", 1));
        var (days, ordinal) = ParseByDay(parts.TryGetValue("BYDAY", out var byDay) ? byDay : null);

        if (ordinal == 0 && parts.TryGetValue("BYSETPOS", out var setPosition) && int.TryParse(setPosition, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            ordinal = position;

        Recurrence recurrence;
        switch (frequency.ToUpperInvariant())
        {
            case "DAILY":
                // "Daily on these days" is a weekly pattern in Exchange, as Outlook writes "every weekday".
                recurrence = days.Count > 0
                    ? new Recurrence.WeeklyPattern(startDate, 1, days.ToArray())
                    : new Recurrence.DailyPattern(startDate, interval);
                break;
            case "WEEKLY":
                if (days.Count == 0)
                    days.Add(ToDayOfTheWeek(startDate.DayOfWeek));

                recurrence = new Recurrence.WeeklyPattern(startDate, interval, days.ToArray());
                break;
            case "MONTHLY":
                recurrence = days.Count > 0
                    ? new Recurrence.RelativeMonthlyPattern(startDate, interval, Collapse(days), ToIndex(ordinal, startDate))
                    : new Recurrence.MonthlyPattern(startDate, interval, DayOfMonth(parts, startDate));
                break;
            case "YEARLY":
                // An EWS yearly pattern has no interval; every n years is every 12n months.
                if (interval > 1)
                {
                    recurrence = days.Count > 0
                        ? new Recurrence.RelativeMonthlyPattern(startDate, interval * 12, Collapse(days), ToIndex(ordinal, startDate))
                        : new Recurrence.MonthlyPattern(startDate, interval * 12, DayOfMonth(parts, startDate));
                    break;
                }

                var month = (Month)Math.Clamp(ParseInt(parts, "BYMONTH", startDate.Month), 1, 12);
                recurrence = days.Count > 0
                    ? new Recurrence.RelativeYearlyPattern(startDate, month, Collapse(days), ToIndex(ordinal, startDate))
                    : new Recurrence.YearlyPattern(startDate, month, DayOfMonth(parts, startDate));
                break;
            default:
                return null;
        }

        if (parts.TryGetValue("COUNT", out var countText) && int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
            recurrence.NumberOfOccurrences = count;
        else if (parts.TryGetValue("UNTIL", out var untilText) && TryParseUntil(untilText, out var until) && until.Date >= startDate)
            recurrence.EndDate = until.Date;
        else
            recurrence.NeverEnds();

        return recurrence;
    }

    private static int ParseInt(Dictionary<string, string> parts, string key, int fallback)
        => parts.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    /// <summary>BYMONTHDAY, where a negative value means the last day; Exchange reads 31 as "the last day of the month".</summary>
    private static int DayOfMonth(Dictionary<string, string> parts, DateTime startDate)
    {
        var day = ParseInt(parts, "BYMONTHDAY", startDate.Day);

        return day < 0 ? 31 : Math.Clamp(day, 1, 31);
    }

    /// <summary>BYDAY as days plus the ordinal ("2TU" = second Tuesday, "-1FR" = last Friday) its entries carry.</summary>
    private static (List<DayOfTheWeek> Days, int Ordinal) ParseByDay(string byDay)
    {
        var days = new List<DayOfTheWeek>();
        var ordinal = 0;

        if (string.IsNullOrWhiteSpace(byDay))
            return (days, ordinal);

        foreach (var entry in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Length < 2)
                continue;

            var index = Array.IndexOf(DayTokens, entry[^2..].ToUpperInvariant());
            if (index < 0)
                continue;

            var day = ToDayOfTheWeek((DayOfWeek)index);
            if (!days.Contains(day))
                days.Add(day);

            if (entry.Length > 2 && int.TryParse(entry[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var entryOrdinal))
                ordinal = entryOrdinal;
        }

        return (days, ordinal);
    }

    /// <summary>
    /// A relative pattern names one day. Exchange has group values for the sets iCalendar lists out:
    /// every day, the weekdays and the weekend days; any other set falls back to its first day.
    /// </summary>
    private static DayOfTheWeek Collapse(List<DayOfTheWeek> days)
    {
        var weekend = days.Count(day => day is DayOfTheWeek.Saturday or DayOfTheWeek.Sunday);

        if (days.Count == 7)
            return DayOfTheWeek.Day;

        if (days.Count == 5 && weekend == 0)
            return DayOfTheWeek.Weekday;

        if (days.Count == 2 && weekend == 2)
            return DayOfTheWeek.WeekendDay;

        return days[0];
    }

    private static DayOfTheWeekIndex ToIndex(int ordinal, DateTime startDate)
    {
        if (ordinal == 0)
            ordinal = ((startDate.Day - 1) / 7) + 1;

        return ordinal switch
        {
            < 0 => DayOfTheWeekIndex.Last,
            1 => DayOfTheWeekIndex.First,
            2 => DayOfTheWeekIndex.Second,
            3 => DayOfTheWeekIndex.Third,
            4 => DayOfTheWeekIndex.Fourth,
            _ => DayOfTheWeekIndex.Last
        };
    }

    private static DayOfTheWeek ToDayOfTheWeek(DayOfWeek day)
        => day switch
        {
            DayOfWeek.Monday => DayOfTheWeek.Monday,
            DayOfWeek.Tuesday => DayOfTheWeek.Tuesday,
            DayOfWeek.Wednesday => DayOfTheWeek.Wednesday,
            DayOfWeek.Thursday => DayOfTheWeek.Thursday,
            DayOfWeek.Friday => DayOfTheWeek.Friday,
            DayOfWeek.Saturday => DayOfTheWeek.Saturday,
            _ => DayOfTheWeek.Sunday
        };

    /// <summary>UNTIL is read as a wall-clock date, as the MAPI encoder reads it.</summary>
    private static bool TryParseUntil(string value, out DateTime until)
        => DateTime.TryParseExact(
            value,
            (string[])["yyyyMMdd", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmss'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out until);
}
