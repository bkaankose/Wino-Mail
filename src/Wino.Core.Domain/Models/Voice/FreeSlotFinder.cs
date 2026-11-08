#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// Picks the start of a free slot for a slot request: the first (or last) gap of the requested length inside each
/// day's window, across every busy interval the device knows. Starts snap to quarter hours and never fall in the past.
/// </summary>
public static class FreeSlotFinder
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    // Without a window or working hours, "my next free spot" means a reasonable time of day, not 3 a.m.
    private static readonly TimeOnly DefaultDayStart = new(8, 0);
    private static readonly TimeOnly DefaultDayEnd = new(20, 0);

    public static DateTime? Find(
        EventSlotRequest request,
        TimeSpan length,
        IEnumerable<(DateTime Start, DateTime End)> busy,
        DateTime now,
        TimeOnly? workDayStart,
        TimeOnly? workDayEnd)
    {
        if (length <= TimeSpan.Zero || request.To < request.From)
            return null;

        var intervals = busy.Where(static b => b.End > b.Start).OrderBy(static b => b.Start).ToList();
        var days = Enumerable.Range(0, request.To.DayNumber - request.From.DayNumber + 1).Select(request.From.AddDays);
        if (request.Preference == SlotPreference.Latest)
            days = days.Reverse();

        foreach (var day in days)
        {
            var (windowStart, windowEnd) = Window(request, workDayStart, workDayEnd);
            var from = day.ToDateTime(windowStart);
            var to = windowEnd == TimeOnly.MinValue ? day.AddDays(1).ToDateTime(TimeOnly.MinValue) : day.ToDateTime(windowEnd);
            if (from < now)
                from = RoundUp(now);
            if (to - from < length)
                continue;

            var gaps = Gaps(from, to, intervals).ToList();
            if (request.Preference == SlotPreference.Latest)
                gaps.Reverse();

            foreach (var (gapStart, gapEnd) in gaps)
            {
                var start = request.Preference == SlotPreference.Earliest ? RoundUp(gapStart) : RoundDown(gapEnd - length);
                if (start >= gapStart && start + length <= gapEnd)
                    return start;
            }
        }

        return null;
    }

    private static (TimeOnly Start, TimeOnly End) Window(EventSlotRequest request, TimeOnly? workDayStart, TimeOnly? workDayEnd)
    {
        var hasWork = workDayStart is { } ws && workDayEnd is { } we && we > ws;
        var start = request.WindowStart ?? (hasWork ? workDayStart!.Value : DefaultDayStart);
        var end = request.WindowEnd ?? (hasWork ? workDayEnd!.Value : DefaultDayEnd);
        if (request.WithinWorkingHours && hasWork)
        {
            start = start > workDayStart!.Value ? start : workDayStart.Value;
            end = request.WindowEnd is null || end > workDayEnd!.Value ? workDayEnd!.Value : end;
        }

        return (start, end);
    }

    private static IEnumerable<(DateTime Start, DateTime End)> Gaps(DateTime from, DateTime to, List<(DateTime Start, DateTime End)> busy)
    {
        var cursor = from;
        foreach (var (start, end) in busy)
        {
            if (end <= cursor)
                continue;
            if (start >= to)
                break;
            if (start > cursor)
                yield return (cursor, start);
            if (end > cursor)
                cursor = end;
        }

        if (cursor < to)
            yield return (cursor, to);
    }

    private static DateTime RoundUp(DateTime value)
    {
        var ticks = (value.Ticks + Step.Ticks - 1) / Step.Ticks * Step.Ticks;
        return new DateTime(ticks, value.Kind);
    }

    private static DateTime RoundDown(DateTime value) => new(value.Ticks / Step.Ticks * Step.Ticks, value.Kind);
}
