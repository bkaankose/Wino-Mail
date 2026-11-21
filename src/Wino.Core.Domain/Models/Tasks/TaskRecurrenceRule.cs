#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Wino.Core.Domain.Models.Tasks;

public enum TaskRecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly
}

/// <summary>
/// The part of an RFC 5545 RRULE the task editor models: frequency, interval, weekdays, day of month and
/// COUNT or UNTIL. A rule with parts it does not model is kept as text and never rewritten by the editor.
/// </summary>
public sealed record TaskRecurrenceRule(
    TaskRecurrenceFrequency Frequency,
    int Interval = 1,
    IReadOnlyList<DayOfWeek>? Days = null,
    int? MonthDay = null,
    int? Count = null,
    DateTime? Until = null,
    int? Month = null)
{
    private static readonly string[] WeekdayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
    private static readonly DayOfWeek[] WorkWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public IReadOnlyList<DayOfWeek> WeekDays => Days ?? [];

    public bool IsWeekdays => Frequency == TaskRecurrenceFrequency.Weekly && Interval == 1 && WeekDays.Count == 5 && WorkWeek.All(WeekDays.Contains);

    /// <summary>Null for an empty rule, a frequency the editor does not model, or a part it cannot read.</summary>
    public static TaskRecurrenceRule? Parse(string? rule)
    {
        var text = TaskFields.NormalizeRecurrence(rule);
        if (text is null)
            return null;

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
                return null;
            parts[pair[0]] = pair[1];
        }

        TaskRecurrenceFrequency frequency;
        switch (parts.GetValueOrDefault("FREQ")?.ToUpperInvariant())
        {
            case "DAILY": frequency = TaskRecurrenceFrequency.Daily; break;
            case "WEEKLY": frequency = TaskRecurrenceFrequency.Weekly; break;
            case "MONTHLY": frequency = TaskRecurrenceFrequency.Monthly; break;
            case "YEARLY": frequency = TaskRecurrenceFrequency.Yearly; break;
            default: return null;
        }

        var known = new[] { "FREQ", "INTERVAL", "BYDAY", "BYMONTHDAY", "COUNT", "UNTIL", "WKST", "BYMONTH" };
        if (parts.Keys.Any(key => !known.Contains(key, StringComparer.OrdinalIgnoreCase)))
            return null;

        var interval = parts.TryGetValue("INTERVAL", out var intervalText) && int.TryParse(intervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInterval) && parsedInterval > 0
            ? parsedInterval
            : 1;

        var days = new List<DayOfWeek>();
        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            foreach (var code in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // An ordinal weekday ("2TU", "-1FR") is a pattern the editor does not model.
                var index = Array.IndexOf(WeekdayCodes, code.ToUpperInvariant());
                if (index < 0)
                    return null;
                days.Add((DayOfWeek)index);
            }
        }

        int? monthDay = parts.TryGetValue("BYMONTHDAY", out var monthDayText) && int.TryParse(monthDayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMonthDay) && parsedMonthDay is >= 1 and <= 31
            ? parsedMonthDay
            : null;
        int? count = parts.TryGetValue("COUNT", out var countText) && int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) && parsedCount > 0
            ? parsedCount
            : null;
        DateTime? until = parts.TryGetValue("UNTIL", out var untilText) &&
                          DateTime.TryParseExact(untilText.Length >= 8 ? untilText[..8] : untilText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedUntil)
            ? parsedUntil
            : null;

        int? month = parts.TryGetValue("BYMONTH", out var monthText) && int.TryParse(monthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMonth) && parsedMonth is >= 1 and <= 12
            ? parsedMonth
            : null;

        if (days.Count > 0 && frequency != TaskRecurrenceFrequency.Weekly ||
            parts.ContainsKey("BYMONTH") && (month is null || frequency != TaskRecurrenceFrequency.Yearly))
        {
            return null;
        }

        return new TaskRecurrenceRule(frequency, interval, days, monthDay, count, until, month);
    }

    public string ToRule()
    {
        var parts = new List<string>
        {
            "FREQ=" + Frequency.ToString().ToUpperInvariant()
        };

        if (Interval > 1)
            parts.Add("INTERVAL=" + Interval.ToString(CultureInfo.InvariantCulture));
        if (WeekDays.Count > 0)
            parts.Add("BYDAY=" + string.Join(',', WeekDays.Distinct().OrderBy(day => ((int)day + 6) % 7).Select(day => WeekdayCodes[(int)day])));
        if (Month is { } month)
            parts.Add("BYMONTH=" + month.ToString(CultureInfo.InvariantCulture));
        if (MonthDay is { } monthDay)
            parts.Add("BYMONTHDAY=" + monthDay.ToString(CultureInfo.InvariantCulture));
        if (Count is { } count)
            parts.Add("COUNT=" + count.ToString(CultureInfo.InvariantCulture));
        else if (Until is { } until)
            parts.Add("UNTIL=" + until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        return string.Join(';', parts);
    }

    /// <summary>The rule of a one-tap preset anchored on <paramref name="anchor"/>; null for "never" or an unknown preset.</summary>
    public static string? Preset(string preset, DateTime anchor)
        => preset switch
        {
            "daily" => new TaskRecurrenceRule(TaskRecurrenceFrequency.Daily).ToRule(),
            "weekdays" => new TaskRecurrenceRule(TaskRecurrenceFrequency.Weekly, Days: WorkWeek).ToRule(),
            "weekly" => new TaskRecurrenceRule(TaskRecurrenceFrequency.Weekly, Days: [anchor.DayOfWeek]).ToRule(),
            "monthly" => new TaskRecurrenceRule(TaskRecurrenceFrequency.Monthly, MonthDay: anchor.Day).ToRule(),
            "yearly" => new TaskRecurrenceRule(TaskRecurrenceFrequency.Yearly, MonthDay: anchor.Day, Month: anchor.Month).ToRule(),
            _ => null
        };

    /// <summary>"Every weekday", "Every 2 weeks on Mon, Thu"; a rule the editor cannot read reads "Repeats".</summary>
    public static string Describe(string? rule, CultureInfo? culture = null)
    {
        if (TaskFields.NormalizeRecurrence(rule) is null)
            return Translator.ToDoPage_Repeat;

        var parsed = Parse(rule);
        if (parsed is null)
            return Translator.ToDoPage_RepeatCustom;

        var formatCulture = culture ?? CultureInfo.CurrentCulture;
        if (parsed.IsWeekdays)
            return Translator.ToDoPage_RepeatEveryWeekday;

        var dayNames = string.Join(", ", parsed.WeekDays
            .OrderBy(day => ((int)day - (int)formatCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7)
            .Select(day => formatCulture.DateTimeFormat.GetAbbreviatedDayName(day)));

        return (parsed.Frequency, parsed.Interval) switch
        {
            (TaskRecurrenceFrequency.Daily, 1) => Translator.ToDoPage_RepeatEveryDay,
            (TaskRecurrenceFrequency.Daily, var n) => string.Format(Translator.ToDoPage_RepeatEveryNDays, n),
            (TaskRecurrenceFrequency.Weekly, 1) when dayNames.Length > 0 => string.Format(Translator.ToDoPage_RepeatEveryWeekOn, dayNames),
            (TaskRecurrenceFrequency.Weekly, 1) => Translator.ToDoPage_RepeatEveryWeek,
            (TaskRecurrenceFrequency.Weekly, var n) when dayNames.Length > 0 => string.Format(Translator.ToDoPage_RepeatEveryNWeeksOn, n, dayNames),
            (TaskRecurrenceFrequency.Weekly, var n) => string.Format(Translator.ToDoPage_RepeatEveryNWeeks, n),
            (TaskRecurrenceFrequency.Monthly, 1) => Translator.ToDoPage_RepeatEveryMonth,
            (TaskRecurrenceFrequency.Monthly, var n) => string.Format(Translator.ToDoPage_RepeatEveryNMonths, n),
            (TaskRecurrenceFrequency.Yearly, 1) => Translator.ToDoPage_RepeatEveryYear,
            (TaskRecurrenceFrequency.Yearly, var n) => string.Format(Translator.ToDoPage_RepeatEveryNYears, n),
            _ => Translator.ToDoPage_RepeatCustom
        };
    }

    /// <summary>
    /// The due day of the occurrence after <paramref name="currentDue"/>, or null when the rule has ended
    /// (UNTIL passed or this was the last COUNT). Weeks start on Monday, the RFC 5545 default.
    /// </summary>
    public DateTime? NextOccurrence(DateTime currentDue)
    {
        if (Count is 1)
            return null;

        var current = currentDue.Date;
        DateTime next;

        switch (Frequency)
        {
            case TaskRecurrenceFrequency.Daily:
                next = current.AddDays(Interval);
                break;
            case TaskRecurrenceFrequency.Weekly when WeekDays.Count > 0:
                var weekStart = StartOfWeek(current);
                next = current;
                for (var offset = 1; offset <= 7 * Interval + 7; offset++)
                {
                    var candidate = current.AddDays(offset);
                    var weeks = (StartOfWeek(candidate) - weekStart).Days / 7;
                    if (weeks % Interval == 0 && WeekDays.Contains(candidate.DayOfWeek))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next == current)
                    return null;
                break;
            case TaskRecurrenceFrequency.Weekly:
                next = current.AddDays(7 * Interval);
                break;
            case TaskRecurrenceFrequency.Monthly:
                var month = current.AddMonths(Interval);
                var day = Math.Min(MonthDay ?? currentDue.Day, DateTime.DaysInMonth(month.Year, month.Month));
                next = new DateTime(month.Year, month.Month, day);
                break;
            default:
                var year = current.Year + Interval;
                var yearMonth = Month ?? current.Month;
                next = new DateTime(year, yearMonth, Math.Min(MonthDay ?? currentDue.Day, DateTime.DaysInMonth(year, yearMonth)));
                break;
        }

        return Until is { } until && next > until.Date ? null : next;
    }

    /// <summary>The rule the next occurrence carries: one fewer COUNT.</summary>
    public TaskRecurrenceRule ForNextOccurrence()
        => Count is { } count ? this with { Count = Math.Max(1, count - 1) } : this;

    private static DateTime StartOfWeek(DateTime date)
        => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).Date;
}
