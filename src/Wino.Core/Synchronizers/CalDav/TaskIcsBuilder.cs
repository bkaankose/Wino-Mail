#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>The fields of one VTODO that Wino maps to a task or a step.</summary>
internal sealed class CalDavTodo
{
    public string Uid { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? Description { get; init; }

    /// <summary>The due day as the user sees it, or null without DUE.</summary>
    public DateTime? DueDate { get; init; }

    public string Status { get; init; } = string.Empty;
    public DateTime? CompletedUtc { get; init; }
    public int? PercentComplete { get; init; }
    public int? Priority { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? DtStampUtc { get; init; }
    public int? Sequence { get; init; }

    /// <summary>The UID named by RELATED-TO without RELTYPE or with RELTYPE=PARENT.</summary>
    public string? ParentUid { get; init; }

    /// <summary>X-APPLE-SORT-ORDER, which Apple Reminders writes to order subtasks.</summary>
    public long? SortOrder { get; init; }

    /// <summary>STATUS decides when present; COMPLETED and PERCENT-COMPLETE left behind by another client do not override it.</summary>
    public bool IsCompleted => string.IsNullOrEmpty(Status)
        ? CompletedUtc.HasValue || PercentComplete == 100
        : string.Equals(Status, "COMPLETED", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 5545 section 3.8.1.9: 1-4 is high priority, 5 medium, 6-9 low, 0 undefined.</summary>
    public bool IsImportant => Priority is >= 1 and <= 4;
}

/// <summary>The VTODO fields a mutation changed; an update patches only these.</summary>
[Flags]
internal enum CalDavTodoFields
{
    None = 0,
    Title = 1,
    Notes = 2,
    Due = 4,
    Completion = 8,
    Importance = 16,
    All = Title | Notes | Due | Completion | Importance
}

/// <summary>What Wino writes into a VTODO. Steps leave the description, due date and priority alone.</summary>
internal sealed class CalDavTodoContent
{
    public CalDavTodoFields Fields { get; init; } = CalDavTodoFields.All;
    public string Uid { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public DateTime? DueDate { get; init; }
    public bool IsCompleted { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public bool IsImportant { get; init; }
    public DateTime? CreatedAtUtc { get; init; }
    public string? ParentUid { get; init; }
    public bool IsStep { get; init; }
}

/// <summary>
/// Reads and writes VTODO resources at the line level. Updates patch only the properties Wino
/// owns and keep every other line byte for byte (RRULE, VALARM, CATEGORIES, X- properties,
/// VTIMEZONE and unknown components), so a round trip through Wino never drops what another
/// client stored. Ical.Net is not used for writing because re-serializing a resource rewrites
/// lines it does not need to touch.
/// </summary>
internal static class TaskIcsBuilder
{
    internal const string ProductId = "-//Wino Mail//Calendar//EN";
    private const int MaximumLineOctets = 75;

    #region Reading

    /// <summary>The master VTODO of every UID in the resource; an overridden instance never wins over its master.</summary>
    public static IReadOnlyList<CalDavTodo> ParseTodos(string? icsContent)
    {
        var result = new List<CalDavTodo>();
        if (string.IsNullOrWhiteSpace(icsContent))
            return result;

        var instances = new List<(CalDavTodo Todo, bool IsOverride)>();
        foreach (var block in FindTodoBlocks(ParseLines(icsContent)))
            instances.Add((ReadTodo(block.Properties), block.Properties.Any(line => line.Name == "RECURRENCE-ID")));

        foreach (var group in instances.Where(item => !string.IsNullOrWhiteSpace(item.Todo.Uid)).GroupBy(item => item.Todo.Uid, StringComparer.Ordinal))
        {
            var master = group.Where(item => !item.IsOverride).Select(item => item.Todo).FirstOrDefault();
            result.Add(master ?? group.First().Todo);
        }

        return result;
    }

    public static CalDavTodo? ParseMasterTodo(string? icsContent) => ParseTodos(icsContent).FirstOrDefault();

    private static CalDavTodo ReadTodo(IReadOnlyList<IcsLine> properties)
    {
        string? Text(string name)
        {
            var line = properties.FirstOrDefault(item => item.Name == name);
            return line == null ? null : UnescapeText(line.Value);
        }

        int? Integer(string name)
            => int.TryParse(properties.FirstOrDefault(item => item.Name == name)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        DateTime? Utc(string name)
        {
            var line = properties.FirstOrDefault(item => item.Name == name);
            return line != null && TryParseDateTime(line.Value, out var value, out _, out _) ? value : null;
        }

        var parent = properties
            .Where(item => item.Name == "RELATED-TO")
            .FirstOrDefault(item =>
            {
                var relType = item.GetParameter("RELTYPE");
                return string.IsNullOrEmpty(relType) || string.Equals(relType, "PARENT", StringComparison.OrdinalIgnoreCase);
            });

        var sortOrderValue = properties.FirstOrDefault(item => item.Name == "X-APPLE-SORT-ORDER")?.Value.Trim();

        return new CalDavTodo
        {
            Uid = properties.FirstOrDefault(item => item.Name == "UID")?.Value.Trim() ?? string.Empty,
            Summary = Text("SUMMARY"),
            Description = Text("DESCRIPTION"),
            DueDate = ReadDueDate(properties.FirstOrDefault(item => item.Name == "DUE")),
            Status = properties.FirstOrDefault(item => item.Name == "STATUS")?.Value.Trim().ToUpperInvariant() ?? string.Empty,
            CompletedUtc = Utc("COMPLETED"),
            PercentComplete = Integer("PERCENT-COMPLETE"),
            Priority = Integer("PRIORITY"),
            CreatedUtc = Utc("CREATED"),
            DtStampUtc = Utc("DTSTAMP"),
            Sequence = Integer("SEQUENCE"),
            ParentUid = string.IsNullOrWhiteSpace(parent?.Value) ? null : parent!.Value.Trim(),
            SortOrder = long.TryParse(sortOrderValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortOrder) ? sortOrder : null
        };
    }

    /// <summary>
    /// A date-only DUE is the day itself. A UTC DUE is shown on the local day it falls on; a
    /// floating or TZID DUE keeps the day as written.
    /// </summary>
    private static DateTime? ReadDueDate(IcsLine? due)
    {
        if (due == null || !TryParseDateTime(due.Value, out var value, out var hasTime, out var isUtc))
            return null;

        if (hasTime && isUtc)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime().Date;

        return DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
    }

    #endregion

    #region Writing

    public static string BuildTodo(CalDavTodoContent content, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);

        var stamp = FormatUtc(nowUtc);
        var created = FormatUtc(content.CreatedAtUtc is { } createdAt && createdAt != default ? ToUtc(createdAt) : nowUtc);

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            $"PRODID:{ProductId}",
            "CALSCALE:GREGORIAN",
            "BEGIN:VTODO",
            $"UID:{content.Uid}",
            $"DTSTAMP:{stamp}",
            $"CREATED:{created}",
            $"LAST-MODIFIED:{stamp}",
            $"SUMMARY:{EscapeText(content.Title)}"
        };

        if (!content.IsStep && !string.IsNullOrWhiteSpace(content.Notes))
            lines.Add($"DESCRIPTION:{EscapeText(content.Notes)}");

        if (!content.IsStep && content.DueDate is { } dueDate)
            lines.Add($"DUE;VALUE=DATE:{FormatDate(dueDate)}");

        lines.AddRange(BuildCompletionLines(content, nowUtc));

        if (!content.IsStep && content.IsImportant)
            lines.Add("PRIORITY:1");

        if (!string.IsNullOrWhiteSpace(content.ParentUid))
            lines.Add($"RELATED-TO;RELTYPE=PARENT:{content.ParentUid}");

        lines.Add("END:VTODO");
        lines.Add("END:VCALENDAR");

        return string.Concat(lines.Select(line => FoldLine(line) + "\r\n"));
    }

    /// <summary>
    /// Applies <paramref name="content"/> to the master VTODO of <paramref name="existingIcs"/>.
    /// Properties whose value already matches are left as they are. Returns null when the
    /// resource holds no VTODO with that UID.
    /// </summary>
    public static string? PatchTodo(string existingIcs, CalDavTodoContent content, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(existingIcs))
            return null;

        var lines = ParseLines(existingIcs);
        var block = FindTodoBlocks(lines)
            .Where(item => string.Equals(item.Properties.FirstOrDefault(line => line.Name == "UID")?.Value.Trim(), content.Uid, StringComparison.Ordinal))
            .OrderBy(item => item.Properties.Any(line => line.Name == "RECURRENCE-ID"))
            .FirstOrDefault();

        if (block == null)
            return null;

        var stored = ReadTodo(block.Properties);
        var fields = content.Fields;
        var replacements = new Dictionary<string, List<string>?>(StringComparer.Ordinal);

        if (fields.HasFlag(CalDavTodoFields.Title) &&
            !string.Equals(stored.Summary ?? string.Empty, content.Title ?? string.Empty, StringComparison.Ordinal))
        {
            replacements["SUMMARY"] = [$"SUMMARY:{EscapeText(content.Title ?? string.Empty)}"];
        }

        if (!content.IsStep)
        {
            var notes = string.IsNullOrEmpty(content.Notes) ? null : content.Notes;
            if (fields.HasFlag(CalDavTodoFields.Notes) &&
                !string.Equals(NormalizeNewLines(stored.Description), NormalizeNewLines(notes), StringComparison.Ordinal))
            {
                replacements["DESCRIPTION"] = notes == null ? null : [$"DESCRIPTION:{EscapeText(notes)}"];
            }

            if (fields.HasFlag(CalDavTodoFields.Due) && stored.DueDate?.Date != content.DueDate?.Date)
                ApplyDueChange(block, content.DueDate, replacements);

            if (fields.HasFlag(CalDavTodoFields.Importance))
            {
                if (content.IsImportant && !stored.IsImportant)
                    replacements["PRIORITY"] = ["PRIORITY:1"];
                else if (!content.IsImportant && stored.IsImportant)
                    replacements["PRIORITY"] = ["PRIORITY:0"];
            }
        }

        // Only a change of completion rewrites STATUS: IN-PROCESS or CANCELLED survive an edit of the title.
        if (fields.HasFlag(CalDavTodoFields.Completion) && stored.IsCompleted != content.IsCompleted)
        {
            replacements["STATUS"] = null;
            replacements["COMPLETED"] = null;
            replacements["PERCENT-COMPLETE"] = null;

            // Completing a recurring VTODO completes the current occurrence only: like Apple
            // Reminders, Nextcloud and tasks.org, the master moves to the next occurrence and stays open.
            if (content.IsCompleted && TryAdvanceRecurrence(lines, block, out var advanced))
            {
                foreach (var pair in advanced)
                    replacements[pair.Key] = pair.Value;

                replacements["STATUS"] = ["STATUS:NEEDS-ACTION"];
            }
            else
            {
                foreach (var line in BuildCompletionLines(content, nowUtc))
                {
                    var name = line[..line.IndexOf(':')];
                    (replacements[name] ??= []).Add(line);
                }
            }
        }

        if (replacements.Count == 0)
            return existingIcs;

        var stamp = FormatUtc(nowUtc);
        replacements["LAST-MODIFIED"] = [$"LAST-MODIFIED:{stamp}"];
        replacements["DTSTAMP"] = [$"DTSTAMP:{stamp}"];
        replacements["SEQUENCE"] = [$"SEQUENCE:{(stored.Sequence ?? 0) + 1}"];

        var output = new StringBuilder(existingIcs.Length + 256);
        for (var index = 0; index < lines.Count; index++)
        {
            if (index == block.InsertIndex)
            {
                foreach (var replacement in replacements.Values.Where(value => value != null).SelectMany(value => value!))
                    output.Append(FoldLine(replacement)).Append("\r\n");
            }

            var line = lines[index];
            if (block.PropertyIndexes.Contains(index) && replacements.ContainsKey(line.Name))
                continue;

            output.Append(line.Raw).Append("\r\n");
        }

        return output.ToString();
    }

    private static IEnumerable<string> BuildCompletionLines(CalDavTodoContent content, DateTime nowUtc)
    {
        if (content.IsCompleted)
        {
            var completedAt = content.CompletedAtUtc is { } value && value != default ? ToUtc(value) : nowUtc;
            yield return "STATUS:COMPLETED";
            yield return "PERCENT-COMPLETE:100";
            yield return $"COMPLETED:{FormatUtc(completedAt)}";
        }
        else
        {
            yield return "STATUS:NEEDS-ACTION";
            yield return "PERCENT-COMPLETE:0";
        }
    }

    /// <summary>
    /// Sets or removes DUE. sabre/vobject (Baikal, Nextcloud) rejects a VTODO whose DUE and DTSTART
    /// differ in value type or whose DUE is not after DTSTART, so a DTSTART that the new DUE does
    /// not follow is removed, or moved before DUE when it anchors a recurrence.
    /// </summary>
    private static void ApplyDueChange(TodoBlock block, DateTime? dueDate, Dictionary<string, List<string>?> replacements)
    {
        if (dueDate is not { } day)
        {
            replacements["DUE"] = null;
            return;
        }

        var storedDue = block.Properties.FirstOrDefault(line => line.Name == "DUE");
        var dtStart = block.Properties.FirstOrDefault(line => line.Name == "DTSTART");
        var dueLine = BuildDueLine(storedDue, dtStart, day);
        replacements["DUE"] = [dueLine];

        // RFC 5545 section 3.6.2: DUE and DURATION must not occur together.
        if (block.Properties.Any(line => line.Name == "DURATION"))
            replacements["DURATION"] = null;

        if (dtStart == null ||
            !TryParseDateTime(dtStart.Value, out var startValue, out var startHasTime, out var startIsUtc) ||
            !TryParseDateTime(IcsLine.Create(dueLine, dueLine).Value, out var dueValue, out var dueHasTime, out var dueIsUtc) ||
            startHasTime != dueHasTime || startIsUtc != dueIsUtc ||
            dueValue > startValue)
        {
            return;
        }

        var isRecurring = block.Properties.Any(line => line.Name is "RRULE" or "RDATE");
        if (!isRecurring)
        {
            replacements["DTSTART"] = null;
            return;
        }

        var movedStart = dueHasTime ? dueValue.AddHours(-1) : dueValue.AddDays(-1);
        replacements["DTSTART"] = [$"{Prefix("DTSTART", dtStart)}:{FormatLike(movedStart, dueHasTime, dueIsUtc)}"];
    }

    /// <summary>
    /// The DUE line for a new due day. DUE follows the value type, time zone and time of day of
    /// the stored DUE, or of DTSTART when there is no DUE or the stored one disagrees with DTSTART;
    /// without either it is date-only.
    /// </summary>
    private static string BuildDueLine(IcsLine? storedDue, IcsLine? dtStart, DateTime dueDate)
    {
        var template = storedDue != null && TryParseDateTime(storedDue.Value, out _, out _, out _) &&
                       (dtStart == null || !TryParseDateTime(dtStart.Value, out _, out _, out _) || HaveSameValueType(storedDue, dtStart))
            ? storedDue
            : dtStart != null && TryParseDateTime(dtStart.Value, out _, out _, out _) ? dtStart : null;

        if (template == null || !TryParseDateTime(template.Value, out var templateValue, out var hasTime, out var isUtc))
            return $"DUE;VALUE=DATE:{FormatDate(dueDate)}";

        var prefix = Prefix("DUE", template);
        if (!hasTime)
            return $"{prefix}:{FormatDate(dueDate)}";

        if (isUtc)
        {
            var local = DateTime.SpecifyKind(templateValue, DateTimeKind.Utc).ToLocalTime();
            var moved = DateTime.SpecifyKind(dueDate.Date + local.TimeOfDay, DateTimeKind.Local).ToUniversalTime();
            return $"{prefix}:{FormatUtc(moved)}";
        }

        return $"{prefix}:{FormatLike(dueDate.Date + templateValue.TimeOfDay, hasTime: true, isUtc: false)}";
    }

    private static bool HaveSameValueType(IcsLine first, IcsLine second)
    {
        if (!TryParseDateTime(first.Value, out _, out var firstHasTime, out var firstIsUtc) ||
            !TryParseDateTime(second.Value, out _, out var secondHasTime, out var secondIsUtc))
        {
            return false;
        }

        return firstHasTime == secondHasTime &&
               firstIsUtc == secondIsUtc &&
               string.Equals(first.GetParameter("TZID") ?? string.Empty, second.GetParameter("TZID") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string Prefix(string name, IcsLine template)
        => string.IsNullOrEmpty(template.Parameters) ? name : $"{name};{template.Parameters}";

    private static string FormatLike(DateTime value, bool hasTime, bool isUtc)
        => !hasTime
            ? FormatDate(value)
            : isUtc
                ? FormatUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                : value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The lines that move a recurring VTODO to its next occurrence: DTSTART and DUE shift by the
    /// same amount and keep their value types, COMPLETED and PERCENT-COMPLETE are removed, and a
    /// COUNT shrinks by the occurrence just completed. False when the VTODO does not recur, has no
    /// DTSTART or DUE to anchor it, or has no occurrence left.
    /// </summary>
    private static bool TryAdvanceRecurrence(IReadOnlyList<IcsLine> lines, TodoBlock block, out Dictionary<string, List<string>?> changes)
    {
        changes = new Dictionary<string, List<string>?>(StringComparer.Ordinal);

        var rules = block.Properties.Where(line => line.Name == "RRULE").ToList();
        var dates = block.Properties.Where(line => line.Name is "RDATE" or "EXDATE").ToList();
        if (rules.Count == 0 && !dates.Any(line => line.Name == "RDATE"))
            return false;

        var dtStart = block.Properties.FirstOrDefault(line => line.Name == "DTSTART");
        var due = block.Properties.FirstOrDefault(line => line.Name == "DUE");
        var anchor = dtStart ?? due;
        if (anchor == null || !TryParseDateTime(anchor.Value, out var anchorValue, out _, out _))
            return false;

        var next = TaskRecurrence.FindNextOccurrence(
            TimeZoneLines(lines),
            FoldLine($"{Prefix("DTSTART", anchor)}:{anchor.Value}"),
            rules.Concat(dates).Select(line => line.Raw).ToList(),
            anchorValue);

        if (next is not { } nextValue)
            return false;

        var delta = nextValue - anchorValue;
        foreach (var line in new[] { dtStart, due })
        {
            if (line == null || !TryParseDateTime(line.Value, out var value, out var hasTime, out var isUtc))
                continue;

            changes[line.Name] = [$"{Prefix(line.Name, line)}:{FormatLike(value + delta, hasTime, isUtc)}"];
        }

        changes["COMPLETED"] = null;
        changes["PERCENT-COMPLETE"] = null;

        if (rules.Any(rule => ReadCount(rule.Value) != null))
            changes["RRULE"] = rules.Select(rule => $"{Prefix("RRULE", rule)}:{DecrementCount(rule.Value)}").ToList();

        return true;
    }

    private static int? ReadCount(string rule)
    {
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 &&
                string.Equals(part[..separator].Trim(), "COUNT", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return count;
            }
        }

        return null;
    }

    private static string DecrementCount(string rule)
        => string.Join(';', rule.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var separator = part.IndexOf('=');
            return separator > 0 &&
                   string.Equals(part[..separator].Trim(), "COUNT", StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(part[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                   count > 1
                ? $"COUNT={(count - 1).ToString(CultureInfo.InvariantCulture)}"
                : part;
        }));

    #endregion

    #region Lines

    private static List<IcsLine> ParseLines(string icsContent)
    {
        var physical = icsContent.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var lines = new List<IcsLine>();
        StringBuilder? logical = null;
        StringBuilder? raw = null;

        void Flush()
        {
            if (logical != null && raw != null)
                lines.Add(IcsLine.Create(logical.ToString(), raw.ToString()));

            logical = null;
            raw = null;
        }

        foreach (var line in physical)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && logical != null && raw != null)
            {
                logical.Append(line, 1, line.Length - 1);
                raw.Append("\r\n").Append(line);
                continue;
            }

            Flush();
            if (line.Length == 0)
                continue;

            logical = new StringBuilder(line);
            raw = new StringBuilder(line);
        }

        Flush();
        return lines;
    }

    /// <summary>VTODO components directly inside VCALENDAR, with their own properties (not those of a VALARM).</summary>
    private static List<TodoBlock> FindTodoBlocks(IReadOnlyList<IcsLine> lines)
    {
        var blocks = new List<TodoBlock>();
        var stack = new Stack<string>();
        TodoBlock? current = null;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.Name == "BEGIN")
            {
                var component = line.Value.Trim().ToUpperInvariant();
                if (current != null && stack.Count > 0 && stack.Peek() == "VTODO" && current.InsertIndex < 0)
                    current.InsertIndex = index;

                if (component == "VTODO" && stack.Count == 1 && stack.Peek() == "VCALENDAR")
                    current = new TodoBlock();

                stack.Push(component);
                continue;
            }

            if (line.Name == "END")
            {
                var component = line.Value.Trim().ToUpperInvariant();
                if (component == "VTODO" && current != null && stack.Count == 2)
                {
                    if (current.InsertIndex < 0)
                        current.InsertIndex = index;

                    blocks.Add(current);
                    current = null;
                }

                if (stack.Count > 0)
                    stack.Pop();

                continue;
            }

            if (current != null && stack.Count == 2 && stack.Peek() == "VTODO")
            {
                current.Properties.Add(line);
                current.PropertyIndexes.Add(index);
            }
        }

        return blocks;
    }

    /// <summary>The raw lines of every VTIMEZONE component, BEGIN and END included.</summary>
    private static List<string> TimeZoneLines(IReadOnlyList<IcsLine> lines)
    {
        var result = new List<string>();
        var depth = 0;

        foreach (var line in lines)
        {
            if (depth == 0 && line.Name == "BEGIN" && string.Equals(line.Value.Trim(), "VTIMEZONE", StringComparison.OrdinalIgnoreCase))
                depth = 1;
            else if (depth > 0 && line.Name == "BEGIN")
                depth++;

            if (depth == 0)
                continue;

            result.Add(line.Raw);

            if (line.Name == "END")
                depth--;
        }

        return result;
    }

    /// <summary>Folds at 75 octets (RFC 5545 section 3.1) without splitting a UTF-8 sequence.</summary>
    internal static string FoldLine(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= MaximumLineOctets)
            return line;

        var builder = new StringBuilder(line.Length + 16);
        var octets = 0;
        var limit = MaximumLineOctets;

        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                builder.Append("\r\n ");
                octets = 0;
                limit = MaximumLineOctets - 1;
            }

            builder.Append(rune.ToString());
            octets += size;
        }

        return builder.ToString();
    }

    #endregion

    #region Values

    internal static string EscapeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    internal static string UnescapeText(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\'))
            return value;

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index == value.Length - 1)
            {
                builder.Append(character);
                continue;
            }

            var next = value[++index];
            builder.Append(next switch
            {
                'n' or 'N' => '\n',
                _ => next
            });
        }

        return builder.ToString();
    }

    private static string? NormalizeNewLines(string? value)
        => string.IsNullOrEmpty(value) ? null : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Parses DATE and DATE-TIME values (RFC 5545 sections 3.3.4 and 3.3.5).</summary>
    private static bool TryParseDateTime(string? value, out DateTime result, out bool hasTime, out bool isUtc)
    {
        result = default;
        hasTime = false;
        isUtc = false;

        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        if (text.Length == 8)
            return DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

        isUtc = text.EndsWith('Z') || text.EndsWith('z');
        var local = isUtc ? text[..^1] : text;
        hasTime = true;

        if (!DateTime.TryParseExact(local, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return false;

        if (isUtc)
            result = DateTime.SpecifyKind(result, DateTimeKind.Utc);

        return true;
    }

    private static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string FormatUtc(DateTime value)
        => ToUtc(value).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime value)
        => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    #endregion

    private sealed class TodoBlock
    {
        public List<IcsLine> Properties { get; } = [];
        public HashSet<int> PropertyIndexes { get; } = [];

        /// <summary>Where new properties go: before the first subcomponent, else before END:VTODO.</summary>
        public int InsertIndex { get; set; } = -1;
    }

    /// <summary>One content line: the unfolded text split into name, parameters and value, and the original folded text.</summary>
    private sealed class IcsLine
    {
        public string Name { get; private init; } = string.Empty;
        public string Parameters { get; private init; } = string.Empty;
        public string Value { get; private init; } = string.Empty;
        public string Raw { get; private init; } = string.Empty;

        public static IcsLine Create(string logical, string raw)
        {
            var inQuotes = false;
            var nameEnd = -1;
            var valueStart = -1;

            for (var index = 0; index < logical.Length; index++)
            {
                var character = logical[index];
                if (character == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (inQuotes)
                    continue;

                if (nameEnd < 0 && (character == ';' || character == ':'))
                    nameEnd = index;

                if (character == ':')
                {
                    valueStart = index + 1;
                    break;
                }
            }

            if (nameEnd < 0 || valueStart < 0)
                return new IcsLine { Name = logical.Trim().ToUpperInvariant(), Raw = raw };

            var parameters = valueStart - 1 > nameEnd + 1 ? logical[(nameEnd + 1)..(valueStart - 1)] : string.Empty;

            return new IcsLine
            {
                Name = logical[..nameEnd].Trim().ToUpperInvariant(),
                Parameters = parameters,
                Value = logical[valueStart..],
                Raw = raw
            };
        }

        public string? GetParameter(string name)
        {
            if (string.IsNullOrEmpty(Parameters))
                return null;

            foreach (var parameter in SplitParameters(Parameters))
            {
                var separator = parameter.IndexOf('=');
                if (separator > 0 && string.Equals(parameter[..separator].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return parameter[(separator + 1)..].Trim().Trim('"');
            }

            return null;
        }

        private static IEnumerable<string> SplitParameters(string parameters)
        {
            var start = 0;
            var inQuotes = false;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index] == '"')
                    inQuotes = !inQuotes;
                else if (parameters[index] == ';' && !inQuotes)
                {
                    yield return parameters[start..index];
                    start = index + 1;
                }
            }

            yield return parameters[start..];
        }
    }
}
