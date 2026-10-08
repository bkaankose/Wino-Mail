using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain;

public static class CalendarRecurrenceSummaryFormatter
{
    private static readonly DayOfWeek[] OrderedDays =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    public static string BuildSummary(
        bool isRecurring,
        DateTimeOffset effectiveStart,
        DateTimeOffset effectiveEnd,
        bool isAllDay,
        CalendarSettings settings,
        int interval,
        CalendarItemRecurrenceFrequency frequency,
        IReadOnlyCollection<DayOfWeek> daysOfWeek,
        DateTimeOffset? recurrenceEndDate)
    {
        var culture = settings?.CultureInfo ?? CultureInfo.CurrentCulture;
        var timeDisplayType = settings?.DayHeaderDisplayType ?? DateTimeDisplayFormatter.GetDefaultTimeDisplayType(culture);

        if (!isRecurring && SpansMultipleDays(effectiveStart, effectiveEnd, isAllDay))
        {
            // All-day ends are exclusive, so the last visible day is the day before the end.
            var startText = isAllDay
                ? effectiveStart.ToString("dddd yyyy-MM-dd", culture)
                : $"{effectiveStart.ToString("dddd yyyy-MM-dd", culture)} {DateTimeDisplayFormatter.FormatTime(effectiveStart.DateTime, timeDisplayType, culture)}";
            var endText = isAllDay
                ? effectiveEnd.AddDays(-1).ToString("dddd yyyy-MM-dd", culture)
                : $"{effectiveEnd.ToString("dddd yyyy-MM-dd", culture)} {DateTimeDisplayFormatter.FormatTime(effectiveEnd.DateTime, timeDisplayType, culture)}";

            return string.Format(culture, Translator.CalendarEventCompose_MultiDayOccurrenceSummary, startText, endText);
        }

        var timeSummary = isAllDay
            ? Translator.CalendarItemAllDay
            : string.Format(
                culture,
                Translator.CalendarEventCompose_TimeRangeSummary,
                DateTimeDisplayFormatter.FormatTime(effectiveStart.DateTime, settings?.DayHeaderDisplayType ?? DateTimeDisplayFormatter.GetDefaultTimeDisplayType(culture), culture),
                DateTimeDisplayFormatter.FormatTime(effectiveEnd.DateTime, settings?.DayHeaderDisplayType ?? DateTimeDisplayFormatter.GetDefaultTimeDisplayType(culture), culture));

        if (!isRecurring)
        {
            return string.Format(
                culture,
                Translator.CalendarEventCompose_SingleOccurrenceSummary,
                effectiveStart.ToString("dddd yyyy-MM-dd", culture),
                timeSummary);
        }

        var normalizedDays = NormalizeDays(daysOfWeek);
        var isEveryDay = (frequency == CalendarItemRecurrenceFrequency.Daily && interval == 1) ||
                         (frequency == CalendarItemRecurrenceFrequency.Weekly && interval == 1 && normalizedDays.Count == 7);

        var cadenceSummary = isEveryDay
            ? $"{Translator.CalendarEventCompose_Every} {Translator.CalendarEventCompose_FrequencyDay}"
            : interval == 1
                ? $"{Translator.CalendarEventCompose_Every} {GetSingularFrequencyLabel(frequency)}"
                : $"{Translator.CalendarEventCompose_Every} {interval.ToString(culture)} {GetPluralFrequencyLabel(frequency)}";

        var weekdaySummary = string.Empty;
        if (frequency == CalendarItemRecurrenceFrequency.Weekly && normalizedDays.Count > 0 && normalizedDays.Count < 7)
        {
            weekdaySummary = string.Format(
                culture,
                Translator.CalendarEventCompose_WeekdaySummary,
                string.Join(", ", normalizedDays.Select(day => culture.DateTimeFormat.GetDayName(day))));
        }

        var untilSummary = recurrenceEndDate.HasValue
            ? string.Format(
                culture,
                Translator.CalendarEventCompose_UntilSummary,
                recurrenceEndDate.Value.ToString("ddd yyyy-MM-dd", culture))
            : string.Empty;

        return string.Format(
            culture,
            Translator.CalendarEventCompose_RecurringSummarySmart,
            cadenceSummary,
            weekdaySummary,
            timeSummary,
            effectiveStart.ToString("dddd yyyy-MM-dd", culture),
            untilSummary).Trim();
    }

    private static bool SpansMultipleDays(DateTimeOffset effectiveStart, DateTimeOffset effectiveEnd, bool isAllDay)
    {
        if (effectiveEnd <= effectiveStart)
            return false;

        if (isAllDay)
            return (effectiveEnd.Date - effectiveStart.Date).TotalDays > 1;

        // An evening event that ends at midnight still belongs to its start day.
        var lastDay = effectiveEnd.TimeOfDay == TimeSpan.Zero ? effectiveEnd.Date.AddDays(-1) : effectiveEnd.Date;
        return lastDay > effectiveStart.Date;
    }

    private static IReadOnlyList<DayOfWeek> NormalizeDays(IReadOnlyCollection<DayOfWeek> daysOfWeek)
    {
        if (daysOfWeek == null || daysOfWeek.Count == 0)
        {
            return [];
        }

        return daysOfWeek
            .Distinct()
            .OrderBy(day => Array.IndexOf(OrderedDays, day))
            .ToList();
    }

    private static string GetSingularFrequencyLabel(CalendarItemRecurrenceFrequency frequency)
    {
        return frequency switch
        {
            CalendarItemRecurrenceFrequency.Daily => Translator.CalendarEventCompose_FrequencyDay,
            CalendarItemRecurrenceFrequency.Weekly => Translator.CalendarEventCompose_FrequencyWeek,
            CalendarItemRecurrenceFrequency.Monthly => Translator.CalendarEventCompose_FrequencyMonth,
            CalendarItemRecurrenceFrequency.Yearly => Translator.CalendarEventCompose_FrequencyYear,
            _ => Translator.CalendarEventCompose_FrequencyWeek
        };
    }

    private static string GetPluralFrequencyLabel(CalendarItemRecurrenceFrequency frequency)
    {
        return frequency switch
        {
            CalendarItemRecurrenceFrequency.Daily => Translator.CalendarEventCompose_FrequencyDayPlural,
            CalendarItemRecurrenceFrequency.Weekly => Translator.CalendarEventCompose_FrequencyWeekPlural,
            CalendarItemRecurrenceFrequency.Monthly => Translator.CalendarEventCompose_FrequencyMonthPlural,
            CalendarItemRecurrenceFrequency.Yearly => Translator.CalendarEventCompose_FrequencyYearPlural,
            _ => Translator.CalendarEventCompose_FrequencyWeekPlural
        };
    }
}


