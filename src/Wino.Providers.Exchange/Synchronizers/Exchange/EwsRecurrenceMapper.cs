using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

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

    /// <summary>
    /// The RRULE of a series master's EWS recurrence, in the form the MAPI transport writes for the same series
    /// (<c>AppointmentRecurrence.ToRRule</c>): UNTIL is the end date at the series' wall-clock start time, floating,
    /// and a day of month of 31 is the last day. <paramref name="firstStartWallClock"/> is the first occurrence's
    /// start in the appointment's own zone. Null for the regeneration patterns, which only tasks carry.
    /// </summary>
    public static string ToRecurrenceRule(Recurrence recurrence, DateTime firstStartWallClock)
    {
        if (recurrence == null)
            return null;

        var parts = new List<string>();
        switch (recurrence)
        {
            case Recurrence.DailyPattern daily:
                parts.Add("FREQ=DAILY");
                parts.Add(Invariant($"INTERVAL={Math.Max(1, daily.Interval)}"));
                break;
            case Recurrence.WeeklyPattern weekly:
                parts.Add("FREQ=WEEKLY");
                parts.Add(Invariant($"INTERVAL={Math.Max(1, weekly.Interval)}"));
                parts.Add("BYDAY=" + ByDay(Enumerable.Range(0, weekly.DaysOfTheWeek.Count).Select(index => weekly.DaysOfTheWeek[index])));
                if (TryGetFirstDayOfWeek(weekly) is { } weekStart)
                    parts.Add("WKST=" + DayTokens[(int)weekStart]);
                break;
            case Recurrence.MonthlyPattern monthly:
                parts.Add("FREQ=MONTHLY");
                parts.Add(Invariant($"INTERVAL={Math.Max(1, monthly.Interval)}"));
                parts.Add("BYMONTHDAY=" + MonthDay(monthly.DayOfMonth));
                break;
            case Recurrence.RelativeMonthlyPattern relativeMonthly:
                parts.Add("FREQ=MONTHLY");
                parts.Add(Invariant($"INTERVAL={Math.Max(1, relativeMonthly.Interval)}"));
                parts.Add("BYDAY=" + ByDay([relativeMonthly.DayOfTheWeek]));
                parts.Add("BYSETPOS=" + SetPosition(relativeMonthly.DayOfTheWeekIndex));
                break;
            case Recurrence.YearlyPattern yearly:
                parts.Add("FREQ=YEARLY");
                parts.Add("INTERVAL=1");
                parts.Add("BYMONTHDAY=" + MonthDay(yearly.DayOfMonth));
                parts.Add(Invariant($"BYMONTH={(int)yearly.Month}"));
                break;
            case Recurrence.RelativeYearlyPattern relativeYearly:
                parts.Add("FREQ=YEARLY");
                parts.Add("INTERVAL=1");
                parts.Add("BYDAY=" + ByDay([relativeYearly.DayOfTheWeek]));
                parts.Add("BYSETPOS=" + SetPosition(relativeYearly.DayOfTheWeekIndex));
                parts.Add(Invariant($"BYMONTH={(int)relativeYearly.Month}"));
                break;
            default:
                return null;
        }

        if (recurrence.NumberOfOccurrences is int count && count > 0)
            parts.Add(Invariant($"COUNT={count}"));
        else if (recurrence.EndDate is DateTime endDate)
            parts.Add("UNTIL=" + (endDate.Date + firstStartWallClock.TimeOfDay).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));

        return "RRULE:" + string.Join(";", parts);
    }

    /// <summary>
    /// Whether two recurrences describe the same series: the same start date and the same rule. The first day of the
    /// week is left out, since a recurrence built here does not carry the one the server holds.
    /// </summary>
    public static bool IsSamePattern(Recurrence current, Recurrence requested, DateTime firstStartWallClock)
    {
        if (current == null || requested == null || current.StartDate.Date != requested.StartDate.Date)
            return false;

        static string WithoutWeekStart(string rule)
            => rule == null ? null : string.Join(";", rule.Split(';').Where(part => !part.StartsWith("WKST=", StringComparison.OrdinalIgnoreCase)));

        var currentRule = WithoutWeekStart(ToRecurrenceRule(current, firstStartWallClock));
        return currentRule != null && currentRule == WithoutWeekStart(ToRecurrenceRule(requested, firstStartWallClock));
    }

    // The server sends FirstDayOfWeek from Exchange 2013 on; the getter throws when the pattern has none.
    private static DayOfWeek? TryGetFirstDayOfWeek(Recurrence.WeeklyPattern weekly)
    {
        try
        {
            return weekly.FirstDayOfWeek;
        }
        catch (ServiceValidationException)
        {
            return null;
        }
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static string MonthDay(int dayOfMonth)
        => dayOfMonth >= 31 ? "-1" : dayOfMonth.ToString(CultureInfo.InvariantCulture);

    private static string SetPosition(DayOfTheWeekIndex index)
        => index switch
        {
            DayOfTheWeekIndex.First => "1",
            DayOfTheWeekIndex.Second => "2",
            DayOfTheWeekIndex.Third => "3",
            DayOfTheWeekIndex.Fourth => "4",
            _ => "-1"
        };

    /// <summary>The days as BYDAY tokens; the group values (every day, weekday, weekend day) are listed out.</summary>
    private static string ByDay(IEnumerable<DayOfTheWeek> days)
    {
        var tokens = new List<string>();
        foreach (var day in days)
        {
            IEnumerable<DayOfWeek> expanded = day switch
            {
                DayOfTheWeek.Day => Enum.GetValues<DayOfWeek>(),
                DayOfTheWeek.Weekday => [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
                DayOfTheWeek.WeekendDay => [DayOfWeek.Sunday, DayOfWeek.Saturday],
                _ => [(DayOfWeek)(int)day]
            };

            foreach (var dayOfWeek in expanded)
            {
                var token = DayTokens[(int)dayOfWeek];
                if (!tokens.Contains(token))
                    tokens.Add(token);
            }
        }

        // MAPI lists the days in week order from Sunday; the same order keeps both transports' rules equal.
        return string.Join(",", tokens.OrderBy(token => Array.IndexOf(DayTokens, token)));
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
