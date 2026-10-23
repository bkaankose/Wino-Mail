#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// One alarm of a server default (<c>calendar-default-alarms</c>, CalendarServer extension).
/// </summary>
/// <param name="Block">The VALARM text to write into a new event, marked with <c>X-APPLE-DEFAULT-ALARM:TRUE</c>.</param>
/// <param name="Reminder">
/// The local reminder the alarm maps to, or null when Mail-Cal cannot express it: a trigger after the
/// start or relative to the end.
/// </param>
internal sealed record CalDavDefaultAlarm(string Block, CalDavEventReminder? Reminder);

/// <summary>
/// Reads the default VALARMs a CalDAV server stores for new events and writes them into an event
/// the way Apple Calendar does.
/// </summary>
internal static partial class CalendarIcsBuilder
{
    internal const string AppleDefaultAlarmProperty = "X-APPLE-DEFAULT-ALARM";

    // Properties that identify one stored alarm instance. A copy written into a new event drops them.
    private static readonly string[] AlarmInstanceProperties = ["UID", "X-WR-ALARMUID", "ACKNOWLEDGED", "RELATED-TO", AppleDefaultAlarmProperty];

    /// <summary>
    /// Parses the raw value of <c>default-alarm-vevent-datetime</c> or <c>-date</c>. Only alarms
    /// with a relative trigger are kept: an absolute trigger has no meaning for another event,
    /// and <c>ACTION:NONE</c> is how Apple stores "no default alarm".
    /// </summary>
    public static IReadOnlyList<CalDavDefaultAlarm> ParseDefaultAlarms(string? valarmText)
    {
        var alarms = new List<CalDavDefaultAlarm>();

        if (string.IsNullOrWhiteSpace(valarmText))
            return alarms;

        List<string>? block = null;

        foreach (var line in UnfoldLines(valarmText))
        {
            if (line.Equals("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                block = [];
                continue;
            }

            if (block == null)
                continue;

            if (line.Equals("END:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                var alarm = ParseAlarmBlock(block);
                if (alarm != null)
                    alarms.Add(alarm);

                block = null;
                continue;
            }

            block.Add(line);
        }

        return alarms;
    }

    /// <summary>
    /// Inserts the alarm blocks into every VEVENT of <paramref name="icsContent"/> that has no
    /// VALARM of its own. The line break style of the content is kept.
    /// </summary>
    public static string AddDefaultAlarms(string icsContent, IReadOnlyList<CalDavDefaultAlarm> alarms)
    {
        if (string.IsNullOrWhiteSpace(icsContent) || alarms == null || alarms.Count == 0)
            return icsContent;

        var newLine = icsContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = icsContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder(icsContent.Length + alarms.Sum(alarm => alarm.Block.Length + 8));
        var insideEvent = false;
        var eventHasAlarm = false;
        var inserted = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Content folded onto a continuation line never starts a component boundary.
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                insideEvent = true;
                eventHasAlarm = false;
            }
            else if (insideEvent && line.Equals("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                eventHasAlarm = true;
            }
            else if (insideEvent && line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (!eventHasAlarm)
                {
                    foreach (var alarm in alarms)
                    {
                        foreach (var alarmLine in alarm.Block.Split('\n'))
                            builder.Append(alarmLine).Append(newLine);
                    }

                    inserted = true;
                }

                insideEvent = false;
            }

            builder.Append(line);

            if (i < lines.Length - 1)
                builder.Append(newLine);
        }

        return inserted ? builder.ToString() : icsContent;
    }

    /// <summary>
    /// Parses an iCalendar duration (RFC 5545 section 3.3.6), for example <c>-PT15M</c>,
    /// <c>P1D</c>, <c>-P1DT9H</c> or <c>-P1W</c>.
    /// </summary>
    internal static bool TryParseIcsDuration(string? value, out TimeSpan duration)
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

    private static CalDavDefaultAlarm? ParseAlarmBlock(List<string> block)
    {
        string? triggerLine = null;
        string? action = null;

        foreach (var line in block)
        {
            var (name, _, value) = SplitContentLine(line);

            if (name.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
                triggerLine = line;
            else if (name.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
                action = value.Trim();
        }

        if (triggerLine == null || string.Equals(action, "NONE", StringComparison.OrdinalIgnoreCase))
            return null;

        var (_, parameters, triggerValue) = SplitContentLine(triggerLine);

        if (HasParameter(parameters, "VALUE", "DATE-TIME") || !TryParseIcsDuration(triggerValue, out var trigger))
            return null;

        var relatedToEnd = HasParameter(parameters, "RELATED", "END");

        // Mail-Cal reminders count seconds before the start, so a trigger after the start or relative
        // to the end is written to the server but has no local reminder.
        CalDavEventReminder? reminder = relatedToEnd || trigger > TimeSpan.Zero
            ? null
            : new CalDavEventReminder
            {
                DurationInSeconds = (int)Math.Min(int.MaxValue, -trigger.TotalSeconds),
                ReminderType = string.Equals(action, "EMAIL", StringComparison.OrdinalIgnoreCase)
                    ? CalendarItemReminderType.Email
                    : CalendarItemReminderType.Popup
            };

        var outputLines = new List<string> { "BEGIN:VALARM" };
        outputLines.AddRange(block
            .Where(line => !AlarmInstanceProperties.Contains(SplitContentLine(line).Name, StringComparer.OrdinalIgnoreCase))
            .SelectMany(FoldLine));
        outputLines.Add($"{AppleDefaultAlarmProperty}:TRUE");
        outputLines.Add("END:VALARM");

        return new CalDavDefaultAlarm(string.Join('\n', outputLines), reminder);
    }

    private static bool HasParameter(IReadOnlyList<string> parameters, string name, string value)
        => parameters.Any(parameter =>
            parameter.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parameter[(name.Length + 1)..].Trim('"'), value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Splits <c>NAME;PARAM=a;PARAM="b:c":value</c> outside quoted parameter values.</summary>
    private static (string Name, IReadOnlyList<string> Parameters, string Value) SplitContentLine(string line)
    {
        var insideQuotes = false;
        var valueStart = -1;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                insideQuotes = !insideQuotes;
            else if (line[i] == ':' && !insideQuotes)
            {
                valueStart = i;
                break;
            }
        }

        var head = valueStart < 0 ? line : line[..valueStart];
        var value = valueStart < 0 ? string.Empty : line[(valueStart + 1)..];
        var parts = head.Split(';');

        return (parts[0].Trim(), parts.Skip(1).ToArray(), value);
    }

    /// <summary>
    /// Normalizes line breaks and joins folded lines (RFC 5545 section 3.1). A value whose every
    /// line is indented came from pretty-printed XML; its lines are only trimmed.
    /// </summary>
    private static IEnumerable<string> UnfoldLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var nonEmptyLines = normalized.Split('\n').Where(line => line.Trim().Length > 0).ToList();

        if (nonEmptyLines.Count > 0 && nonEmptyLines.All(line => line[0] is ' ' or '\t'))
        {
            foreach (var line in nonEmptyLines)
                yield return line.Trim();

            yield break;
        }

        var current = new StringBuilder();
        var hasCurrent = false;

        foreach (var rawLine in normalized.Split('\n'))
        {
            if (rawLine.Length > 0 && rawLine[0] is ' ' or '\t' && hasCurrent)
            {
                current.Append(rawLine, 1, rawLine.Length - 1);
                continue;
            }

            if (hasCurrent && current.Length > 0)
                yield return current.ToString().Trim();

            current.Clear().Append(rawLine);
            hasCurrent = true;
        }

        if (hasCurrent && current.Length > 0)
            yield return current.ToString().Trim();
    }

    /// <summary>Folds a content line at 75 characters, continuation lines starting with a space.</summary>
    private static IEnumerable<string> FoldLine(string line)
    {
        const int limit = 75;

        if (line.Length <= limit)
        {
            yield return line;
            yield break;
        }

        yield return line[..limit];

        for (var index = limit; index < line.Length; index += limit - 1)
            yield return " " + line.Substring(index, Math.Min(limit - 1, line.Length - index));
    }
}
