using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Graph.Models;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;
using Wino.Providers.Outlook.Extensions;

namespace Wino.Providers.Outlook.Helpers;

/// <summary>
/// Maps the todoTask fields beyond title, notes, due date and completion: reminder, importance levels,
/// categories, start date and recurrence.
/// </summary>
internal static class TaskFieldMapper
{
    private static readonly ILogger Logger = Log.ForContext(typeof(TaskFieldMapper));

    public static void ReadInto(AccountTask task, TodoTask remote)
    {
        if (remote is null)
            return;

        var priority = remote.Importance switch
        {
            Importance.High => TaskPriority.High,
            Importance.Low => TaskPriority.Low,
            Importance.Normal => TaskPriority.Normal,
            _ => TaskPriority.None
        };
        TaskFields.SetPriority(task, priority);

        task.ReminderAtUtc = remote.IsReminderOn == true ? ToUtc(remote.ReminderDateTime) : null;
        task.Categories = TaskFields.JoinCategories(remote.Categories ?? []);
        task.StartDate = OutlookIntegratorExtensions.GetTaskDueDate(remote.StartDateTime);
        task.Recurrence = ReadRecurrence(remote.Recurrence);
    }

    /// <summary>
    /// Adds the extended fields to a payload. A create sends every field that is set; an update sends only the
    /// fields that changed from <paramref name="original"/>, so a value Wino cannot read (a recurrence pattern
    /// it does not map) is never overwritten by an edit of something else.
    /// </summary>
    public static void WriteInto(TodoTask payload, AccountTask task, AccountTask original, bool isCreate)
    {
        payload.Importance = TaskFields.GetEffectivePriority(task) switch
        {
            TaskPriority.High => Importance.High,
            TaskPriority.Low => Importance.Low,
            _ => Importance.Normal
        };

        bool Changed<T>(Func<AccountTask, T> value) => isCreate || original is null || !EqualityComparer<T>.Default.Equals(value(original), value(task));

        if (Changed(item => item.ReminderAtUtc))
        {
            payload.IsReminderOn = task.ReminderAtUtc.HasValue;
            if (task.ReminderAtUtc is { } reminder)
                payload.ReminderDateTime = ToDateTimeTimeZone(reminder);
        }

        if (isCreate ? !string.IsNullOrEmpty(task.Categories) : original is null || !TaskFields.CategoriesEqual(original.Categories, task.Categories))
            payload.Categories = TaskFields.GetCategories(task).ToList();

        if (Changed(item => item.StartDate?.Date))
        {
            if (task.StartDate is { } start)
                payload.StartDateTime = OutlookIntegratorExtensions.ToTaskDueDateTime(start);
            else if (!isCreate)
                ClearProperty(payload, "startDateTime");
        }

        var rule = TaskFields.NormalizeRecurrence(task.Recurrence);
        var recurrenceChanged = Changed(item => TaskFields.NormalizeRecurrence(item.Recurrence)) ||
                                rule is not null && Changed(item => item.DueDate?.Date);
        if (recurrenceChanged)
        {
            var recurrence = rule is null ? null : CreateRecurrence(rule, task.DueDate);
            if (recurrence is not null)
                payload.Recurrence = recurrence;
            else if (!isCreate)
                ClearProperty(payload, "recurrence");
        }
    }

    /// <summary>The request adapter leaves null properties out, so a cleared value goes as an explicit JSON null.</summary>
    private static void ClearProperty(TodoTask payload, string name)
    {
        payload.AdditionalData ??= new Dictionary<string, object>();
        payload.AdditionalData[name] = null;
    }

    private static DateTime? ToUtc(DateTimeTimeZone value)
    {
        if (string.IsNullOrEmpty(value?.DateTime) ||
            !DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var zone = !string.IsNullOrWhiteSpace(value.TimeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(value.TimeZone, out var found)
            ? found
            : TimeZoneInfo.Utc;

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone);
    }

    private static DateTimeTimeZone ToDateTimeTimeZone(DateTime utc)
        => new()
        {
            DateTime = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            TimeZone = "UTC"
        };

    private static string ReadRecurrence(PatternedRecurrence recurrence)
    {
        if (recurrence?.Pattern is null)
            return null;

        try
        {
            return TaskFields.NormalizeRecurrence(recurrence.ToRfc5545RecurrenceString());
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            Logger.Information("A To Do recurrence pattern {Type} has no RRULE mapping; it is left as it is on the server.", recurrence.Pattern.Type);
            return null;
        }
    }

    /// <summary>Graph anchors a task recurrence on the due date, so a rule without one is not sent.</summary>
    private static PatternedRecurrence CreateRecurrence(string rule, DateTime? dueDate)
    {
        if (dueDate is not { } due)
            return null;

        var zone = TimeZoneInfo.Local;
        var zoneId = zone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windowsId) ? windowsId : zone.Id;

        PatternedRecurrence recurrence;
        try
        {
            recurrence = CalendarRecurrenceMapper.CreateOutlookRecurrence(new CalendarItem
            {
                Recurrence = "RRULE:" + rule,
                StartDate = due.Date,
                StartTimeZone = zoneId
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (recurrence?.Range is null)
            return recurrence;

        var count = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && string.Equals(parts[0], "COUNT", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .FirstOrDefault();

        if (count > 0)
        {
            recurrence.Range.Type = RecurrenceRangeType.Numbered;
            recurrence.Range.NumberOfOccurrences = count;
        }

        return recurrence;
    }
}
