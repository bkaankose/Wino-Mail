#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Synchronizers.CalDav;

namespace Wino.Core.Services;

/// <summary>
/// iCalendar export and import go through the VTODO writer and reader the CalDAV engine uses, so a file
/// carries the same fields a CalDAV server receives. CSV follows RFC 4180.
/// </summary>
public sealed class TaskTransferService : ITaskTransferService
{
    private static readonly string[] CsvColumns =
        ["List", "Title", "Notes", "Due date", "Reminder", "Completed", "Completed at", "Priority", "Categories", "Start date", "Repeat", "Steps"];

    private const string StepDonePrefix = "[x] ";
    private const string StepOpenPrefix = "[ ] ";

    public string ExportIcs(IReadOnlyList<AccountTask> tasks, string calendarName)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var now = DateTime.UtcNow;
        var output = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n")
            .Append("PRODID:").Append(TaskIcsBuilder.ProductId).Append("\r\n")
            .Append("CALSCALE:GREGORIAN\r\n");

        if (!string.IsNullOrWhiteSpace(calendarName))
            output.Append(TaskIcsBuilder.FoldLine("X-WR-CALNAME:" + TaskIcsBuilder.EscapeText(calendarName))).Append("\r\n");

        foreach (var task in tasks)
        {
            var uid = task.Id.ToString("N");
            AppendTodo(output, TaskIcsBuilder.BuildTodo(new CalDavTodoContent
            {
                Uid = uid,
                Title = task.Title ?? string.Empty,
                Notes = task.Notes,
                DueDate = task.DueDate?.Date,
                IsCompleted = task.IsCompleted,
                CompletedAtUtc = task.CompletedAtUtc,
                Priority = TaskFields.GetEffectivePriority(task),
                ReminderUtc = task.ReminderAtUtc,
                Categories = TaskFields.GetCategories(task),
                StartDate = task.StartDate?.Date,
                RecurrenceRule = TaskFields.NormalizeRecurrence(task.Recurrence),
                CreatedAtUtc = task.CreatedAtUtc
            }, now));

            foreach (var step in (task.Steps ?? []).OrderBy(step => step.Order))
            {
                AppendTodo(output, TaskIcsBuilder.BuildTodo(new CalDavTodoContent
                {
                    Uid = step.Id.ToString("N"),
                    Title = step.Title ?? string.Empty,
                    IsCompleted = step.IsCompleted,
                    CreatedAtUtc = step.CreatedAtUtc,
                    ParentUid = uid,
                    IsStep = true
                }, now));
            }
        }

        return output.Append("END:VCALENDAR\r\n").ToString();
    }

    /// <summary>Copies the VTODO component out of a single-component calendar.</summary>
    private static void AppendTodo(StringBuilder output, string calendar)
    {
        var start = calendar.IndexOf("BEGIN:VTODO", StringComparison.Ordinal);
        var end = calendar.LastIndexOf("END:VTODO", StringComparison.Ordinal);
        if (start < 0 || end < start)
            return;

        output.Append(calendar, start, end - start).Append("END:VTODO\r\n");
    }

    public IReadOnlyList<AccountTask> ImportIcs(string content)
    {
        var todos = TaskIcsBuilder.ParseTodos(content);
        var parents = todos.Where(todo => string.IsNullOrWhiteSpace(todo.ParentUid) || todos.All(other => other.Uid != todo.ParentUid)).ToList();
        var result = new List<AccountTask>(parents.Count);

        foreach (var todo in parents)
        {
            var task = new AccountTask
            {
                Title = string.IsNullOrWhiteSpace(todo.Summary) ? string.Empty : todo.Summary.Trim(),
                Notes = todo.Description ?? string.Empty,
                DueDate = todo.DueDate,
                IsCompleted = todo.IsCompleted,
                CompletedAtUtc = todo.IsCompleted ? todo.CompletedUtc ?? DateTime.UtcNow : null,
                ReminderAtUtc = todo.ReminderUtc,
                Categories = TaskFields.JoinCategories(todo.Categories),
                StartDate = todo.StartDate,
                Recurrence = todo.RecurrenceRule
            };
            TaskFields.SetPriority(task, todo.PriorityLevel);

            var order = 0;
            task.Steps = todos
                .Where(child => string.Equals(child.ParentUid, todo.Uid, StringComparison.Ordinal))
                .OrderBy(child => child.SortOrder ?? long.MaxValue)
                .Select(child => new AccountTaskStep { Title = child.Summary ?? string.Empty, IsCompleted = child.IsCompleted, Order = order++ })
                .ToList();

            result.Add(task);
        }

        return result;
    }

    public string ExportCsv(IReadOnlyList<AccountTask> tasks, IReadOnlyDictionary<Guid, string> listNames)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var output = new StringBuilder();
        AppendRow(output, CsvColumns);

        foreach (var task in tasks)
        {
            AppendRow(output,
            [
                listNames is not null && listNames.TryGetValue(task.TaskListId, out var listName) ? listName : string.Empty,
                task.Title ?? string.Empty,
                task.Notes ?? string.Empty,
                task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                task.ReminderAtUtc is { } reminder ? DateTime.SpecifyKind(reminder, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : string.Empty,
                task.IsCompleted ? "Yes" : "No",
                task.CompletedAtUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : string.Empty,
                TaskFields.GetEffectivePriority(task) is var priority && priority != TaskPriority.None ? priority.ToString() : string.Empty,
                string.Join("; ", TaskFields.GetCategories(task)),
                task.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                TaskFields.NormalizeRecurrence(task.Recurrence) ?? string.Empty,
                string.Join("\n", (task.Steps ?? []).OrderBy(step => step.Order).Select(step => (step.IsCompleted ? StepDonePrefix : StepOpenPrefix) + step.Title))
            ]);
        }

        return output.ToString();
    }

    public IReadOnlyList<AccountTask> ImportCsv(string content)
    {
        var rows = ParseCsv(content ?? string.Empty);
        if (rows.Count == 0)
            return [];

        var header = rows[0].Select(name => name.Trim()).ToList();
        int Column(string name) => header.FindIndex(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase));
        string Cell(IReadOnlyList<string> row, string name)
        {
            var index = Column(name);
            return index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;
        }

        if (Column("Title") < 0)
            return [];

        var result = new List<AccountTask>();
        foreach (var row in rows.Skip(1))
        {
            var title = Cell(row, "Title");
            if (string.IsNullOrEmpty(title))
                continue;

            var isCompleted = Cell(row, "Completed").ToUpperInvariant() is "YES" or "TRUE" or "1" or "X";
            var task = new AccountTask
            {
                Title = title,
                Notes = Cell(row, "Notes"),
                DueDate = ParseDate(Cell(row, "Due date")),
                ReminderAtUtc = ParseInstant(Cell(row, "Reminder")),
                IsCompleted = isCompleted,
                CompletedAtUtc = isCompleted ? ParseInstant(Cell(row, "Completed at")) ?? DateTime.UtcNow : null,
                Categories = TaskFields.JoinCategories(Cell(row, "Categories").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                StartDate = ParseDate(Cell(row, "Start date")),
                Recurrence = TaskFields.NormalizeRecurrence(Cell(row, "Repeat"))
            };
            TaskFields.SetPriority(task, Enum.TryParse<TaskPriority>(Cell(row, "Priority"), ignoreCase: true, out var priority) ? priority : TaskPriority.None);

            var order = 0;
            task.Steps = Cell(row, "Steps")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.StartsWith(StepDonePrefix.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? new AccountTaskStep { Title = line[StepDonePrefix.Trim().Length..].Trim(), IsCompleted = true, Order = order++ }
                    : new AccountTaskStep { Title = line.StartsWith(StepOpenPrefix.Trim(), StringComparison.Ordinal) ? line[StepOpenPrefix.Trim().Length..].Trim() : line, Order = order++ })
                .Where(step => step.Title.Length > 0)
                .ToList();

            result.Add(task);
        }

        return result;
    }

    private static DateTime? ParseDate(string value)
        => string.IsNullOrEmpty(value)
            ? null
            : DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ? exact.Date
            : DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local) ? local.Date
            : null;

    /// <summary>An ISO 8601 instant; a value without an offset is read as local time.</summary>
    private static DateTime? ParseInstant(string value)
        => string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed.UtcDateTime
            : null;

    private static void AppendRow(StringBuilder output, IReadOnlyList<string> cells)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            if (index > 0)
                output.Append(',');

            var cell = cells[index] ?? string.Empty;
            if (cell.IndexOfAny([',', '"', '\n', '\r']) >= 0)
                output.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else
                output.Append(cell);
        }

        output.Append("\r\n");
    }

    private static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var text = content.TrimStart('﻿');

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else if (character != '\r')
                {
                    cell.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
