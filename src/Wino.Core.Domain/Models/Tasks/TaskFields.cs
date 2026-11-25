using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Tasks;

/// <summary>
/// The rules for the task fields added beside the original ones (reminder, priority, categories, start
/// date, recurrence): how they are copied, how a server value merges with a local-only one, and how the
/// importance star and the priority stay in step.
/// </summary>
public static class TaskFields
{
    private const char CategorySeparator = '\n';

    /// <summary>
    /// The priority the task means. The star wins, because older code and other surfaces only change
    /// <see cref="AccountTask.IsImportant"/>; a High priority whose star was turned off reads as none.
    /// </summary>
    public static TaskPriority GetEffectivePriority(AccountTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsImportant)
            return TaskPriority.High;

        return task.Priority == TaskPriority.High ? TaskPriority.None : task.Priority;
    }

    public static void SetPriority(AccountTask task, TaskPriority priority)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.Priority = priority;
        task.IsImportant = priority == TaskPriority.High;
    }

    public static IReadOnlyList<string> GetCategories(AccountTask task)
        => ParseCategories(task?.Categories);

    public static IReadOnlyList<string> ParseCategories(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(CategorySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>Null for no categories, so an empty list and a missing value compare equal.</summary>
    public static string? JoinCategories(IEnumerable<string> categories)
    {
        var names = (categories ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Replace(CategorySeparator, ' ').Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? null : string.Join(CategorySeparator, names);
    }

    public static bool CategoriesEqual(string? first, string? second)
        => GetCategoryKey(first) == GetCategoryKey(second);

    private static string GetCategoryKey(string? value)
        => string.Join(CategorySeparator, ParseCategories(value).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)).ToUpperInvariant();

    public static string? NormalizeRecurrence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var rule = value.Trim();
        if (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            rule = rule["RRULE:".Length..];

        return string.IsNullOrWhiteSpace(rule) ? null : rule.Trim();
    }

    /// <summary>Copies the extended fields, used by every place that clones a task row.</summary>
    public static void CopyExtendedFields(AccountTask source, AccountTask target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        target.ReminderAtUtc = source.ReminderAtUtc;
        target.ReminderSnoozedUntilUtc = source.ReminderSnoozedUntilUtc;
        target.Priority = source.Priority;
        target.Categories = source.Categories;
        target.StartDate = source.StartDate;
        target.Recurrence = source.Recurrence;
    }

    /// <summary>
    /// Writes the server's value of each extended field the source stores into <paramref name="target"/>,
    /// and the value from <paramref name="localSource"/> for each field it does not, so a server change
    /// never wipes a value that only this device holds. The snooze is always local.
    /// </summary>
    public static void MergeServerFields(AccountTask target, AccountTask server, AccountTask localSource, TaskCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(capabilities);

        localSource ??= target;

        var reminderChanged = target.ReminderAtUtc != (capabilities.SupportsReminder ? server.ReminderAtUtc : localSource.ReminderAtUtc);
        target.ReminderAtUtc = capabilities.SupportsReminder ? server.ReminderAtUtc : localSource.ReminderAtUtc;
        target.ReminderSnoozedUntilUtc = reminderChanged ? null : localSource.ReminderSnoozedUntilUtc;

        if (capabilities.SupportsPriorityLevels)
        {
            SetPriority(target, GetEffectivePriority(server));
        }
        else
        {
            target.Priority = localSource.Priority;
            if (!capabilities.SupportsImportance)
                target.IsImportant = localSource.IsImportant;
        }

        target.Categories = capabilities.SupportsCategories ? server.Categories : localSource.Categories;
        target.StartDate = capabilities.SupportsStartDate ? server.StartDate?.Date : localSource.StartDate;
        target.Recurrence = capabilities.SupportsRecurrence ? NormalizeRecurrence(server.Recurrence) : localSource.Recurrence;
    }

    /// <summary>
    /// The default reminder of a new task with a due date: the due day at nine in the morning, moved
    /// earlier by the "task reminder timing" setting. Null when the setting is off or the time has passed.
    /// </summary>
    public static DateTime? GetDefaultReminderUtc(DateTime? dueDate, bool remindersEnabled, TaskReminderTiming timing, DateTime nowUtc)
    {
        if (!remindersEnabled || dueDate is not { } due)
            return null;

        var local = DateTime.SpecifyKind(due.Date.AddHours(DefaultReminderHour), DateTimeKind.Local) - timing switch
        {
            TaskReminderTiming.FifteenMinutesBefore => TimeSpan.FromMinutes(15),
            TaskReminderTiming.OneHourBefore => TimeSpan.FromHours(1),
            _ => TimeSpan.Zero
        };

        var utc = local.ToUniversalTime();
        return utc > nowUtc ? utc : null;
    }

    /// <summary>The time of day a due date stands for when a reminder needs one.</summary>
    public const int DefaultReminderHour = 9;
}
