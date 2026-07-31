using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Helpers;

public static class CalendarRecurrenceMapper
{
    public static PatternedRecurrence CreateOutlookRecurrence(CalendarItem calendarItem)
    {
        if (calendarItem == null || string.IsNullOrWhiteSpace(calendarItem.Recurrence))
            return null;

        var ruleLine = calendarItem.Recurrence
            .Split(Domain.Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(ruleLine))
            return null;

        var components = ruleLine["RRULE:".Length..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].ToUpperInvariant(), parts => parts[1], StringComparer.OrdinalIgnoreCase);

        if (!components.TryGetValue("FREQ", out var frequency))
            return null;

        var pattern = new RecurrencePattern
        {
            Interval = ParseInt(components, "INTERVAL", 1),
            FirstDayOfWeek = DayOfWeekObject.Monday
        };

        var byDays = ParseByDays(components);
        var startDate = calendarItem.StartDate;

        switch (frequency.ToUpperInvariant())
        {
            case "DAILY":
                pattern.Type = RecurrencePatternType.Daily;
                break;
            case "WEEKLY":
                pattern.Type = RecurrencePatternType.Weekly;
                    pattern.DaysOfWeek = byDays.Any()
                        ? byDays.Select(day => (DayOfWeekObject?)day).ToList()
                        : [(DayOfWeekObject?)MapDay(startDate.DayOfWeek)];
                break;
            case "MONTHLY":
                if (byDays.Any())
                {
                    pattern.Type = RecurrencePatternType.RelativeMonthly;
                    pattern.DaysOfWeek = byDays.Select(day => (DayOfWeekObject?)day).ToList();
                    pattern.Index = MapWeekIndex(startDate);
                }
                else
                {
                    pattern.Type = RecurrencePatternType.AbsoluteMonthly;
                    pattern.DayOfMonth = ParseInt(components, "BYMONTHDAY", startDate.Day);
                }
                break;
            case "YEARLY":
                pattern.Month = ParseInt(components, "BYMONTH", startDate.Month);

                if (byDays.Any())
                {
                    pattern.Type = RecurrencePatternType.RelativeYearly;
                    pattern.DaysOfWeek = byDays.Select(day => (DayOfWeekObject?)day).ToList();
                    pattern.Index = MapWeekIndex(startDate);
                }
                else
                {
                    pattern.Type = RecurrencePatternType.AbsoluteYearly;
                    pattern.DayOfMonth = ParseInt(components, "BYMONTHDAY", startDate.Day);
                }
                break;
            default:
                return null;
        }

        var recurrenceRange = CreateRange(components, calendarItem);
        return new PatternedRecurrence
        {
            Pattern = pattern,
            Range = recurrenceRange
        };
    }

    private static RecurrenceRange CreateRange(IReadOnlyDictionary<string, string> components, CalendarItem calendarItem)
    {
        var startDate = CreateDate(calendarItem.StartDate);

        if (components.TryGetValue("UNTIL", out var untilValue) &&
            TryParseUntil(untilValue, out var untilDate))
        {
            return new RecurrenceRange
            {
                Type = RecurrenceRangeType.EndDate,
                StartDate = startDate,
                EndDate = CreateDate(untilDate),
                RecurrenceTimeZone = calendarItem.StartTimeZone
            };
        }

        return new RecurrenceRange
        {
            Type = RecurrenceRangeType.NoEnd,
            StartDate = startDate,
            RecurrenceTimeZone = calendarItem.StartTimeZone
        };
    }

    private static bool TryParseUntil(string untilValue, out DateTime untilDate)
    {
        untilDate = default;

        if (string.IsNullOrWhiteSpace(untilValue))
            return false;

        return DateTime.TryParseExact(
                   untilValue,
                   ["yyyyMMdd", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmss'Z'"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out untilDate)
               || DateTime.TryParse(untilValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out untilDate);
    }

    private static List<DayOfWeekObject> ParseByDays(IReadOnlyDictionary<string, string> components)
    {
        if (!components.TryGetValue("BYDAY", out var byDayValue) || string.IsNullOrWhiteSpace(byDayValue))
            return [];

        return byDayValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MapDay)
            .ToList();
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> components, string key, int fallback)
        => components.TryGetValue(key, out var value) && int.TryParse(value, out var parsedValue) ? parsedValue : fallback;

    private static DayOfWeekObject MapDay(string dayToken)
    {
        return dayToken.ToUpperInvariant() switch
        {
            "MO" => DayOfWeekObject.Monday,
            "TU" => DayOfWeekObject.Tuesday,
            "WE" => DayOfWeekObject.Wednesday,
            "TH" => DayOfWeekObject.Thursday,
            "FR" => DayOfWeekObject.Friday,
            "SA" => DayOfWeekObject.Saturday,
            "SU" => DayOfWeekObject.Sunday,
            _ => throw new ArgumentOutOfRangeException(nameof(dayToken), dayToken, null)
        };
    }

    private static DayOfWeekObject MapDay(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => DayOfWeekObject.Monday,
            DayOfWeek.Tuesday => DayOfWeekObject.Tuesday,
            DayOfWeek.Wednesday => DayOfWeekObject.Wednesday,
            DayOfWeek.Thursday => DayOfWeekObject.Thursday,
            DayOfWeek.Friday => DayOfWeekObject.Friday,
            DayOfWeek.Saturday => DayOfWeekObject.Saturday,
            DayOfWeek.Sunday => DayOfWeekObject.Sunday,
            _ => DayOfWeekObject.Monday
        };
    }

    private static WeekIndex MapWeekIndex(DateTime date)
    {
        var occurrence = ((date.Day - 1) / 7) + 1;

        return occurrence switch
        {
            1 => WeekIndex.First,
            2 => WeekIndex.Second,
            3 => WeekIndex.Third,
            4 => WeekIndex.Fourth,
            _ => WeekIndex.Last
        };
    }

    private static Date CreateDate(DateTime dateTime) => new(dateTime.Year, dateTime.Month, dateTime.Day);
}
