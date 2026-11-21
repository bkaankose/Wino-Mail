using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Messaging.UI;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Helpers;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Reminder, priority and categories of the selected task. Each edit is one task update through the
/// request pipeline; a field the list's provider does not store is kept on this device by the commit.
/// </summary>
public partial class ToDoPageViewModel
{
    /// <summary>The reminder a preset stands for, in local time; null for "none" or an unknown preset.</summary>
    public static DateTime? ReminderPresetLocal(string preset, DateTime nowLocal)
    {
        var morning = TimeSpan.FromHours(TaskFields.DefaultReminderHour);
        return preset switch
        {
            // The next full hour at least three hours away, as other task apps place "later today".
            "latertoday" => nowLocal.Date.AddHours(nowLocal.Hour + 3 + (nowLocal.Minute > 0 ? 1 : 0)),
            "tomorrow" => nowLocal.Date.AddDays(1) + morning,
            "nextweek" => nowLocal.Date.AddDays(7) + morning,
            _ => null
        };
    }

    [RelayCommand]
    private Task SetReminderPresetAsync(string preset)
    {
        if (SelectedTask is null || !CanEditSelectedTask)
            return Task.CompletedTask;

        return SetTaskReminderAsync(SelectedTask, ReminderPresetLocal(preset, DateTime.Now));
    }

    /// <summary>Sets or clears the reminder from a picked local date and time.</summary>
    public Task SetTaskReminderAsync(TaskItemViewModel item, DateTime? reminderLocal)
        => UpdateTaskFieldsAsync(item, desired =>
        {
            desired.ReminderAtUtc = reminderLocal is { } local
                ? DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()
                : null;
            desired.ReminderSnoozedUntilUtc = null;
        });

    [RelayCommand]
    private Task SetPriorityAsync(string priority)
    {
        if (SelectedTask is null || !CanEditSelectedTask || !Enum.TryParse<TaskPriority>(priority, ignoreCase: true, out var value))
            return Task.CompletedTask;

        return UpdateTaskFieldsAsync(SelectedTask, desired => TaskFields.SetPriority(desired, value));
    }

    public Task AddTaskCategoryAsync(TaskItemViewModel item, string category)
    {
        var name = category?.Trim();
        if (string.IsNullOrEmpty(name) || item is null)
            return Task.CompletedTask;

        return UpdateTaskFieldsAsync(item, desired =>
            desired.Categories = TaskFields.JoinCategories(TaskFields.GetCategories(desired).Append(name)));
    }

    [RelayCommand]
    private Task RemoveTaskCategoryAsync(string category)
    {
        if (SelectedTask is null || !CanEditSelectedTask || string.IsNullOrWhiteSpace(category))
            return Task.CompletedTask;

        return UpdateTaskFieldsAsync(SelectedTask, desired =>
            desired.Categories = TaskFields.JoinCategories(
                TaskFields.GetCategories(desired).Where(name => !string.Equals(name, category, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Category names used by any loaded task, for the category box's suggestions.</summary>
    public List<string> GetCategorySuggestions(string query)
    {
        var text = query?.Trim() ?? string.Empty;

        return _menuCountTasks.Values
            .Concat(_taskItems.Values.Select(item => item.Task))
            .SelectMany(TaskFields.GetCategories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => text.Length == 0 || name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(name => SelectedTask is null || !SelectedTask.CategoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToList();
    }

    private readonly Dictionary<Guid, string> _pendingNextOccurrences = [];

    [RelayCommand]
    private Task SetRepeatPresetAsync(string preset)
    {
        if (SelectedTask is null || !CanEditSelectedTask)
            return Task.CompletedTask;

        var anchor = SelectedTask.DueDate ?? DateTime.Now.Date;
        return SetTaskRecurrenceAsync(SelectedTask, TaskRecurrenceRule.Preset(preset, anchor));
    }

    /// <summary>
    /// Sets or clears the repeat rule. A repeating task needs a due date to count from, so a task without one
    /// becomes due today.
    /// </summary>
    public Task SetTaskRecurrenceAsync(TaskItemViewModel item, string rule)
        => UpdateTaskFieldsAsync(item, desired =>
        {
            desired.Recurrence = TaskFields.NormalizeRecurrence(rule);
            if (desired.Recurrence is not null && desired.DueDate is null)
                desired.DueDate = DateTime.Now.Date;
        });

    private void TrackNextOccurrence(TaskCompletionPlanner.Plan plan)
    {
        if (plan.NextOccurrence?.Task is { } next)
            _pendingNextOccurrences[next.Id] = next.Title ?? string.Empty;
    }

    /// <summary>A reverted create of a next occurrence failed on the server; the completed task stays completed.</summary>
    private void ReportFailedNextOccurrence(TaskStateChanged message)
    {
        if (message.Operation != TaskSynchronizerOperation.CreateTask ||
            message.Task is null ||
            !_pendingNextOccurrences.Remove(message.Task.Id, out var title))
        {
            return;
        }

        _dialogService.InfoBarMessage(
            Translator.ToDoPage_NextOccurrenceFailedTitle,
            string.Format(Translator.ToDoPage_NextOccurrenceFailedMessage, title),
            InfoBarMessageType.Warning);
    }

    private async Task UpdateTaskFieldsAsync(TaskItemViewModel item, Action<AccountTask> change)
    {
        if (item is null || item.IsReadOnly)
            return;

        var original = RequestEntityCloner.Task(item.Task);
        var desired = RequestEntityCloner.Task(item.Task);
        change(desired);

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)).ConfigureAwait(false);
    }
}
