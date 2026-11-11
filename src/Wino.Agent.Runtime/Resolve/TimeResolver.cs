using System.Globalization;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>A time ref as the runtime uses it: wall clock in the user's zone, validated.</summary>
public sealed record ResolvedTime(DateTime? Start, DateTime? End, bool AllDay, IReadOnlyList<string> Assumed, string? TimeZoneId, bool Vague, string? Recurrence)
{
    public static readonly ResolvedTime Empty = new(null, null, false, [], null, false, null);
}

/// <summary>
/// The classifier computes times in wall clock; the runtime only validates (end after start), applies <c>assumed</c>,
/// resolves <c>vague</c> and <c>partOfDay</c> from the clock preferences, and <c>tzOrCity</c> through <see cref="TimeZoneInfo"/>.
/// Free slots are found against the device's busy intervals (the old FreeSlotFinder, moved here).
/// </summary>
public static class TimeResolver
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    public static DateTime? Parse(string? wall)
    {
        if (string.IsNullOrWhiteSpace(wall)) return null;
        if (DateTime.TryParse(wall, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        if (DateOnly.TryParse(wall, CultureInfo.InvariantCulture, out var d)) return d.ToDateTime(TimeOnly.MinValue);
        return null;
    }

    public static ResolvedTime Resolve(AssistantRef r, IAgentClock clock)
    {
        var t = r.Time;
        if (t is null) return ResolvedTime.Empty;
        var start = Parse(t.Start);
        var end = Parse(t.End);
        var assumed = (t.Assumed ?? []).ToList();
        var allDay = t.AllDay || (start is { } s0 && s0.TimeOfDay == TimeSpan.Zero && end is { } e0 && e0.TimeOfDay == TimeSpan.Zero && t.Start?.Contains('T') != true);
        if (start is { } s && end is { } e && e < s)
        {
            end = s.Date == e.Date || allDay ? s.Date.AddDays(1) : s.AddHours(1);
            assumed.Add("end");
        }

        if (start is not null && end is null && !allDay && r.By == AssistantRefBy.Point)
        {
            end = start.Value.AddHours(1);
            if (!assumed.Contains("duration")) assumed.Add("duration");
        }

        var vague = t.Vague is not null && start is null;
        if (vague || (t.PartOfDay is not null && start is { } ps && ps.TimeOfDay == TimeSpan.Zero && !t.AllDay))
        {
            var day = (start ?? clock.LocalNow).Date;
            var at = PartOfDay(t.PartOfDay ?? t.Vague ?? "", clock);
            if (at is { } tod)
            {
                start = day.Add(tod.ToTimeSpan());
                if (start < clock.LocalNow && t.Dir != "past") start = start.Value.AddDays(1);
                end = start.Value.AddHours(1);
                assumed.Add("time");
                vague = false;
            }
        }

        string? tz = null;
        if (!string.IsNullOrEmpty(t.TzOrCity))
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(t.TzOrCity).Id; }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return new ResolvedTime(start, end, allDay, assumed, tz, vague, t.Recurrence);
    }

    /// <summary>A clock-derived time for a canonical part of the day; null when there is no rule (the turn asks).</summary>
    public static TimeOnly? PartOfDay(string concept, IAgentClock clock)
    {
        var workStart = clock.WorkDayStart ?? new TimeOnly(9, 0);
        var workEnd = clock.WorkDayEnd ?? new TimeOnly(18, 0);
        var noon = new TimeOnly(12, 0);
        return concept switch
        {
            "morning" => workStart,
            "noon" or "midday" or "lunch" => noon,
            "afternoon" => noon.AddHours(2),
            "evening" => workEnd,
            "night" => workEnd.AddHours(3),
            "soon" or "now" => TimeOnly.FromDateTime(RoundUp(clock.LocalNow.AddMinutes(30))),
            _ => null,
        };
    }

    /// <summary>The first (or last) gap of the requested length inside each day's window. Starts snap to quarter hours and never fall in the past.</summary>
    public static DateTime? FindFreeSlot(SlotRequest request, IEnumerable<AgentBusyInterval> busy, DateTime now, TimeOnly? workDayStart, TimeOnly? workDayEnd)
    {
        var length = TimeSpan.FromMinutes(request.Minutes);
        if (length <= TimeSpan.Zero || request.To < request.From) return null;
        var intervals = busy.Where(b => b.End > b.Start).OrderBy(b => b.Start).ToList();
        var from = DateOnly.FromDateTime(request.From);
        var to = DateOnly.FromDateTime(request.To);
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays);
        if (request.Latest) days = days.Reverse();
        var hasWork = workDayStart is { } ws && workDayEnd is { } we && we > ws;
        var windowStart = request.From.TimeOfDay != TimeSpan.Zero ? TimeOnly.FromTimeSpan(request.From.TimeOfDay) : hasWork && request.WithinWorkingHours ? workDayStart!.Value : hasWork ? workDayStart!.Value : new TimeOnly(8, 0);
        var windowEnd = request.To.TimeOfDay != TimeSpan.Zero ? TimeOnly.FromTimeSpan(request.To.TimeOfDay) : hasWork ? workDayEnd!.Value : new TimeOnly(20, 0);
        foreach (var day in days)
        {
            var dayFrom = day.ToDateTime(windowStart);
            var dayTo = windowEnd <= windowStart ? day.AddDays(1).ToDateTime(TimeOnly.MinValue) : day.ToDateTime(windowEnd);
            if (dayFrom < now) dayFrom = RoundUp(now);
            if (dayTo - dayFrom < length) continue;
            var gaps = Gaps(dayFrom, dayTo, intervals).ToList();
            if (request.Latest) gaps.Reverse();
            foreach (var (gapStart, gapEnd) in gaps)
            {
                var start = request.Latest ? RoundDown(gapEnd - length) : RoundUp(gapStart);
                if (start >= gapStart && start + length <= gapEnd) return start;
            }
        }

        return null;
    }

    private static IEnumerable<(DateTime Start, DateTime End)> Gaps(DateTime from, DateTime to, List<AgentBusyInterval> busy)
    {
        var cursor = from;
        foreach (var b in busy)
        {
            if (b.End <= cursor) continue;
            if (b.Start >= to) break;
            if (b.Start > cursor) yield return (cursor, b.Start);
            if (b.End > cursor) cursor = b.End;
        }

        if (cursor < to) yield return (cursor, to);
    }

    public static DateTime RoundUp(DateTime value) => new((value.Ticks + Step.Ticks - 1) / Step.Ticks * Step.Ticks, value.Kind);

    public static DateTime RoundDown(DateTime value) => new(value.Ticks / Step.Ticks * Step.Ticks, value.Kind);
}
