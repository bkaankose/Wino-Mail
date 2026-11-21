using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests.Tasks;

namespace Wino.Mail.ViewModels;

/// <summary>Copying a list's tasks to another list, on this account or another one.</summary>
public partial class ToDoPageViewModel
{
    /// <summary>
    /// Copies every task of the selected list into a list the user picks, through that list's create pipeline.
    /// Fields the destination does not store stay on this device with the copy. Steps are created after
    /// their task, where the destination has steps.
    /// </summary>
    [RelayCommand]
    private async Task CopySelectedListAsync()
    {
        var source = SelectedList;
        if (source is null || _taskMutationService is null)
            return;

        var destinations = TaskLists.Where(list => !list.IsReadOnly && list.Id != source.Id).ToList();
        if (destinations.Count == 0)
            return;

        var destination = await _dialogService.ShowTaskListPickerDialogAsync(destinations, Accounts.ToList(), Translator.ToDoPage_CopyListTo).ConfigureAwait(false);
        if (destination is null)
            return;

        var tasks = await _taskService.GetTasksAsync(listId: source.Id).ConfigureAwait(false) ?? [];
        if (tasks.Count == 0)
            return;

        var capabilities = await _taskService.GetTaskCapabilitiesAsync(destination.Id).ConfigureAwait(false);
        var requests = BuildCopyRequests(tasks, destination, capabilities);

        if (!await QueueRequestsAsync(destination.MailAccountId, requests).ConfigureAwait(false))
            return;

        _dialogService.InfoBarMessage(
            Translator.ToDoPage_CopyListTo,
            string.Format(Translator.ToDoPage_CopyListStarted, tasks.Count, destination.Title),
            InfoBarMessageType.Information);
    }

    /// <summary>A create per task, then a create per step of each task where the destination keeps steps.</summary>
    public static List<TaskActionRequest> BuildCopyRequests(IEnumerable<AccountTask> tasks, AccountTaskList destination, TaskCapabilities capabilities)
    {
        var taskRequests = new List<TaskActionRequest>();
        var stepRequests = new List<TaskActionRequest>();

        foreach (var task in tasks)
        {
            var copy = new AccountTask
            {
                MailAccountId = destination.MailAccountId,
                TaskListId = destination.Id,
                SourceKind = destination.SourceKind,
                Title = task.Title,
                Notes = task.Notes,
                DueDate = task.DueDate,
                IsCompleted = task.IsCompleted,
                CompletedAtUtc = task.CompletedAtUtc,
                IsImportant = task.IsImportant,
                MyDayDateUtc = task.MyDayDateUtc,
                Priority = task.Priority,
                ReminderAtUtc = task.ReminderAtUtc,
                Categories = task.Categories,
                StartDate = task.StartDate,
                Recurrence = task.Recurrence
            };

            taskRequests.Add(new TaskActionRequest(destination.MailAccountId, TaskSynchronizerOperation.CreateTask, Task: copy));

            if (!capabilities.SupportsSteps)
                continue;

            var order = 0;
            foreach (var step in (task.Steps ?? []).OrderBy(step => step.Order))
            {
                stepRequests.Add(new TaskActionRequest(destination.MailAccountId, TaskSynchronizerOperation.CreateStep, Task: copy, Step: new AccountTaskStep
                {
                    TaskId = copy.Id,
                    MailAccountId = destination.MailAccountId,
                    SourceKind = destination.SourceKind,
                    Title = step.Title,
                    IsCompleted = step.IsCompleted,
                    Order = order++
                }));
            }
        }

        return [.. taskRequests, .. stepRequests];
    }
}
