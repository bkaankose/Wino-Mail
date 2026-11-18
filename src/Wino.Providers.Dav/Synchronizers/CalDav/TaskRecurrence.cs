#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ical.Net;
using Serilog;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>Finds the next occurrence of a recurring VTODO with the recurrence engine the calendar uses.</summary>
internal static class TaskRecurrence
{
    private const int MaximumOccurrences = 5000;

    private static readonly ILogger Logger = Log.ForContext(typeof(TaskRecurrence));
    private static readonly Regex TimeZoneParameter = new(";TZID=(\"[^\"]*\"|[^;:]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first occurrence after <paramref name="anchorValue"/>, as written in the anchor's own
    /// value type and time zone, or null when the rule has no occurrence left.
    /// </summary>
    /// <param name="timeZoneLines">Raw VTIMEZONE lines of the resource.</param>
    /// <param name="dtStartLine">The anchor written as a DTSTART line.</param>
    /// <param name="recurrenceLines">Raw RRULE, RDATE and EXDATE lines.</param>
    public static DateTime? FindNextOccurrence(
        IReadOnlyList<string> timeZoneLines,
        string dtStartLine,
        IReadOnlyList<string> recurrenceLines,
        DateTime anchorValue)
    {
        try
        {
            return Evaluate(timeZoneLines, dtStartLine, recurrenceLines, anchorValue);
        }
        catch (Exception ex)
        {
            Logger.Information(ex, "Could not evaluate a VTODO recurrence in its time zone; evaluating it in floating time");
        }

        // Recurrence rules apply to wall-clock time, so a time zone Ical.Net cannot resolve gives the
        // same next occurrence when evaluated as floating time.
        try
        {
            return Evaluate(
                [],
                TimeZoneParameter.Replace(dtStartLine, string.Empty),
                recurrenceLines.Select(line => TimeZoneParameter.Replace(line, string.Empty)).ToList(),
                anchorValue);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not evaluate a VTODO recurrence");
            return null;
        }
    }

    private static DateTime? Evaluate(
        IReadOnlyList<string> timeZoneLines,
        string dtStartLine,
        IReadOnlyList<string> recurrenceLines,
        DateTime anchorValue)
    {
        var text = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n")
            .Append("PRODID:").Append(TaskIcsBuilder.ProductId).Append("\r\n");

        foreach (var line in timeZoneLines)
            text.Append(line).Append("\r\n");

        text.Append("BEGIN:VEVENT\r\nUID:wino-task-recurrence\r\nDTSTAMP:20000101T000000Z\r\n")
            .Append(dtStartLine).Append("\r\n");

        foreach (var line in recurrenceLines)
            text.Append(line).Append("\r\n");

        text.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");

        var calendarEvent = Calendar.Load(text.ToString())?.Events.FirstOrDefault()
                            ?? throw new FormatException("The recurrence could not be read.");

        foreach (var occurrence in calendarEvent.GetOccurrences(null, null).Take(MaximumOccurrences))
        {
            var start = occurrence.Period.StartTime;
            if (start != null && start.Value > anchorValue)
                return start.Value;
        }

        return null;
    }
}
