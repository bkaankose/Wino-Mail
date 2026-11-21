#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Services;

/// <summary>
/// The VALARMs Wino writes for its reminders (RFC 5545 section 3.6.6). Wino owns an alarm when its
/// trigger is a duration relative to the start and its action is DISPLAY or AUDIO: that is what a
/// notification reminder reads back as. Every other alarm (an absolute trigger, a trigger relative to
/// the end, EMAIL, an unknown action) is kept as the server stored it.
/// </summary>
public static class CalDavAlarms
{
    /// <summary>The trigger for seconds before the start: <c>-PT15M</c> before, <c>PT10M</c> after, <c>PT0S</c> at the start.</summary>
    public static string FormatTrigger(long offsetSeconds)
    {
        var trigger = -offsetSeconds;
        var sign = trigger < 0 ? "-" : string.Empty;
        var seconds = Math.Abs(trigger);

        if (seconds == 0)
            return "PT0S";

        if (seconds % (7 * 86400) == 0)
            return $"{sign}P{seconds / (7 * 86400)}W";

        var builder = new StringBuilder(sign).Append('P');
        var days = seconds / 86400;
        var rest = seconds % 86400;

        if (days > 0)
            builder.Append(days).Append('D');

        if (rest > 0)
        {
            builder.Append('T');
            if (rest / 3600 > 0)
                builder.Append(rest / 3600).Append('H');
            if (rest % 3600 / 60 > 0)
                builder.Append(rest % 3600 / 60).Append('M');
            if (rest % 60 > 0)
                builder.Append(rest % 60).Append('S');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses an iCalendar duration (RFC 5545 section 3.3.6), for example <c>-PT15M</c>,
    /// <c>P1D</c>, <c>-P1DT9H</c> or <c>-P1W</c>.
    /// </summary>
    public static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim().ToUpperInvariant();
        var sign = 1;

        if (text[0] is '+' or '-')
        {
            sign = text[0] == '-' ? -1 : 1;
            text = text[1..];
        }

        if (text.Length < 2 || text[0] != 'P')
            return false;

        long seconds = 0;
        var inTime = false;
        var number = new StringBuilder();
        var sawComponent = false;

        foreach (var character in text[1..])
        {
            if (char.IsAsciiDigit(character))
            {
                number.Append(character);
                continue;
            }

            if (character == 'T')
            {
                if (inTime || number.Length > 0)
                    return false;

                inTime = true;
                continue;
            }

            if (number.Length == 0 || !long.TryParse(number.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                return false;

            number.Clear();

            long? unit = (character, inTime) switch
            {
                ('W', false) => 7 * 86400,
                ('D', false) => 86400,
                ('H', true) => 3600,
                ('M', true) => 60,
                ('S', true) => 1,
                _ => null
            };

            if (unit == null)
                return false;

            seconds += amount * unit.Value;
            sawComponent = true;
        }

        if (!sawComponent || number.Length > 0)
            return false;

        duration = TimeSpan.FromSeconds(sign * seconds);
        return true;
    }

    /// <summary>The unfolded lines of a new VALARM for a notification reminder.</summary>
    public static IEnumerable<string> BuildAlarm(long offsetSeconds, string? summary)
    {
        yield return "BEGIN:VALARM";
        yield return "ACTION:DISPLAY";
        yield return $"TRIGGER:{FormatTrigger(offsetSeconds)}";
        yield return $"DESCRIPTION:{CalDavIcsLines.EscapeText(string.IsNullOrWhiteSpace(summary) ? "Reminder" : summary)}";
        yield return "END:VALARM";
    }

    /// <summary>The offsets the server should hold for <paramref name="reminders"/>: notification reminders, nearest first.</summary>
    public static List<long> ToAlarmOffsets(IEnumerable<Reminder>? reminders)
        => (reminders ?? [])
            .Where(reminder => reminder != null && reminder.ReminderType == CalendarItemReminderType.Popup)
            .Select(reminder => reminder.DurationInSeconds)
            .Distinct()
            .OrderBy(offset => Math.Abs(offset))
            .ToList();

    /// <summary>
    /// The seconds before the start of an alarm Wino owns, given the alarm's own properties; null for
    /// an alarm Wino leaves alone.
    /// </summary>
    internal static long? ReadOwnedOffset(IEnumerable<IcsLine> alarmProperties)
    {
        string? action = null;
        IcsLine? trigger = null;

        foreach (var line in alarmProperties)
        {
            if (line.Name == "ACTION")
                action = line.Value.Trim();
            else if (line.Name == "TRIGGER")
                trigger = line;
        }

        if (trigger == null ||
            !(action == null || action.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) || action.Equals("AUDIO", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(trigger.GetParameter("VALUE"), "DATE-TIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trigger.GetParameter("RELATED"), "END", StringComparison.OrdinalIgnoreCase) ||
            !TryParseDuration(trigger.Value, out var duration))
        {
            return null;
        }

        return (long)-duration.TotalSeconds;
    }

    /// <summary>The alarms directly inside the component between <paramref name="beginIndex"/> and <paramref name="endIndex"/>.</summary>
    internal static List<(int Begin, int End, long? Offset)> FindAlarms(IReadOnlyList<IcsLine> lines, int beginIndex, int endIndex)
    {
        var alarms = new List<(int, int, long?)>();
        var depth = 0;
        var alarmBegin = -1;

        for (var index = beginIndex + 1; index < endIndex; index++)
        {
            var line = lines[index];
            if (line.Name == "BEGIN")
            {
                if (depth == 0 && string.Equals(line.Value.Trim(), "VALARM", StringComparison.OrdinalIgnoreCase))
                    alarmBegin = index;

                depth++;
                continue;
            }

            if (line.Name != "END")
                continue;

            depth--;
            if (depth == 0 && alarmBegin >= 0)
            {
                var properties = new List<IcsLine>();
                for (var inner = alarmBegin + 1; inner < index; inner++)
                    properties.Add(lines[inner]);

                alarms.Add((alarmBegin, index, ReadOwnedOffset(properties)));
                alarmBegin = -1;
            }
        }

        return alarms;
    }

    /// <summary>The offsets of the alarms Wino owns in the component, sorted.</summary>
    internal static List<long> ReadOwnedOffsets(IReadOnlyList<IcsLine> lines, int beginIndex, int endIndex)
        => FindAlarms(lines, beginIndex, endIndex)
            .Where(alarm => alarm.Offset.HasValue)
            .Select(alarm => alarm.Offset!.Value)
            .OrderBy(offset => offset)
            .ToList();

    public static bool SameOffsets(IEnumerable<long> left, IEnumerable<long> right)
        => left.OrderBy(value => value).SequenceEqual(right.OrderBy(value => value));

    /// <summary>
    /// The raw lines of one component (BEGIN to END) with the alarms Wino owns changed to
    /// <paramref name="offsets"/>. An owned alarm whose offset is still wanted keeps its lines; the
    /// others are removed, and missing ones are added before the component's END.
    /// </summary>
    internal static List<string> RewriteOwnedAlarms(IEnumerable<string> rawComponentLines, IReadOnlyCollection<long> offsets, string? summary)
    {
        var lines = CalDavIcsLines.Parse(string.Join("\r\n", rawComponentLines));
        var output = new List<string>(lines.Count + offsets.Count * 5);
        if (lines.Count == 0)
            return output;

        var endIndex = lines.Count - 1;
        var alarms = FindAlarms(lines, 0, endIndex);
        var remaining = offsets.ToList();
        var skipped = new HashSet<int>();

        foreach (var alarm in alarms.Where(alarm => alarm.Offset.HasValue))
        {
            if (remaining.Remove(alarm.Offset!.Value))
                continue;

            for (var index = alarm.Begin; index <= alarm.End; index++)
                skipped.Add(index);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (index == endIndex)
            {
                foreach (var offset in remaining)
                    output.AddRange(BuildAlarm(offset, summary).Select(CalDavIcsLines.Fold));
            }

            if (!skipped.Contains(index))
                output.Add(lines[index].Raw);
        }

        return output;
    }

    /// <summary>
    /// Writes <paramref name="reminders"/> as alarms into every VEVENT of a new resource built by Wino.
    /// </summary>
    public static string AddAlarms(string icsContent, IEnumerable<Reminder>? reminders, string? summary)
    {
        var offsets = ToAlarmOffsets(reminders);
        if (string.IsNullOrWhiteSpace(icsContent) || offsets.Count == 0)
            return icsContent;

        var lines = CalDavIcsLines.Parse(icsContent);
        var output = new List<string>(lines.Count + offsets.Count * 5);
        var blocks = CalDavIcsLines.TopLevelComponents(lines).Where(block => block.Name == "VEVENT").ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            var block = blocks.FirstOrDefault(candidate => candidate.BeginIndex == index);
            if (block == null)
            {
                output.Add(lines[index].Raw);
                continue;
            }

            var raw = new List<string>();
            for (var inner = block.BeginIndex; inner <= block.EndIndex; inner++)
                raw.Add(lines[inner].Raw);

            output.AddRange(RewriteOwnedAlarms(raw, offsets, summary));
            index = block.EndIndex;
        }

        return CalDavIcsLines.Join(output);
    }
}
