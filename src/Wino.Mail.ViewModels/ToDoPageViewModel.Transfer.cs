using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests.Tasks;

namespace Wino.Mail.ViewModels;

/// <summary>Copying a list's tasks to another list, on this account or another one.</summary>
public partial class ToDoPageViewModel
{
    private readonly ITaskTransferService _taskTransferService;

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

        // The notice goes up first: the result can arrive before the queue call returns.
        StartTransfer(Translator.ToDoPage_CopyListTo, destination, requests);
        ShowNotice(
            Translator.ToDoPage_CopyListTo,
            string.Format(Translator.ToDoPage_CopyListStarted, tasks.Count, destination.Title),
            InfoBarMessageType.Information,
            destination.Id);

        if (!await QueueRequestsAsync(destination.MailAccountId, requests).ConfigureAwait(false))
        {
            _pendingTransfer = null;
            await ExecuteUIThread(() => IsNoticeOpen = false).ConfigureAwait(false);
        }
    }

    /// <summary>Saves the selected list, or every task when no list is selected, as "ics" or "csv".</summary>
    [RelayCommand]
    private async Task ExportTasksAsync(string format)
    {
        if (_taskTransferService is null)
            return;

        var list = SelectedList;
        var tasks = await _taskService.GetTasksAsync(listId: list?.Id).ConfigureAwait(false) ?? [];
        var isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        var name = SanitizeFileName(list?.Title ?? Translator.ToDoPage_Tasks) + (isCsv ? ".csv" : ".ics");

        var path = await _dialogService.PickFilePathAsync(name).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
            return;

        var content = isCsv
            ? _taskTransferService.ExportCsv(tasks, TaskLists.ToDictionary(item => item.Id, item => item.Title ?? string.Empty))
            : _taskTransferService.ExportIcs(tasks, list?.Title ?? Translator.ToDoPage_Tasks);

        try
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isCsv)).ConfigureAwait(false);
            ShowNotice(Translator.ToDoPage_ExportTasks, string.Format(Translator.ToDoPage_ExportDone, tasks.Count, path), InfoBarMessageType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Translator.ToDoPage_ExportTasks, ex.Message, InfoBarMessageType.Error);
        }
    }

    /// <summary>Reads an .ics or .csv file into the selected list, or a list the user picks, through that list's pipeline.</summary>
    [RelayCommand]
    private async Task ImportTasksAsync()
    {
        if (_taskTransferService is null)
            return;

        var files = await _dialogService.PickFilesAsync(".ics", ".csv").ConfigureAwait(false);
        if (files is null || files.Count == 0)
            return;

        var destination = SelectedList is { IsReadOnly: false } selected
            ? selected
            : await _dialogService.ShowTaskListPickerDialogAsync(TaskLists.Where(list => !list.IsReadOnly).ToList(), Accounts.ToList(), Translator.ToDoPage_ImportTasks).ConfigureAwait(false);
        if (destination is null)
            return;

        var imported = new List<AccountTask>();
        foreach (var file in files)
        {
            var text = Encoding.UTF8.GetString(file.Data ?? []);
            imported.AddRange(file.FileExtension == ".csv" ? _taskTransferService.ImportCsv(text) : _taskTransferService.ImportIcs(text));
        }

        if (imported.Count == 0)
        {
            ShowNotice(Translator.ToDoPage_ImportTasks, Translator.ToDoPage_ImportNothing, InfoBarMessageType.Warning);
            return;
        }

        var capabilities = await _taskService.GetTaskCapabilitiesAsync(destination.Id).ConfigureAwait(false);
        var requests = BuildCopyRequests(imported, destination, capabilities);

        // The notice goes up first: the result can arrive before the queue call returns.
        StartTransfer(Translator.ToDoPage_ImportTasks, destination, requests);
        ShowNotice(
            Translator.ToDoPage_ImportTasks,
            string.Format(Translator.ToDoPage_ImportStarted, imported.Count, destination.Title),
            InfoBarMessageType.Information,
            destination.Id);

        if (!await QueueRequestsAsync(destination.MailAccountId, requests).ConfigureAwait(false))
        {
            _pendingTransfer = null;
            await ExecuteUIThread(() => IsNoticeOpen = false).ConfigureAwait(false);
        }
    }

    /// <summary>The creates of a copy or import that is under way, so its notice can report the result.</summary>
    private sealed record PendingTransfer(string Title, Guid AccountId, Guid ListId, string ListTitle, HashSet<Guid> TaskIds, HashSet<Guid> FailedIds);

    private PendingTransfer _pendingTransfer;

    private void StartTransfer(string title, AccountTaskList destination, IEnumerable<TaskActionRequest> requests)
        => _pendingTransfer = new PendingTransfer(
            title,
            destination.MailAccountId,
            destination.Id,
            destination.Title ?? string.Empty,
            requests.Where(request => request.Operation == TaskSynchronizerOperation.CreateTask && request.Task is not null)
                .Select(request => request.Task.Id)
                .ToHashSet(),
            []);

    /// <summary>A create of the running copy or import was reverted: the server refused it.</summary>
    private void TrackTransferRevert(TaskStateChanged message)
    {
        if (_pendingTransfer is { } transfer &&
            message.Operation == TaskSynchronizerOperation.CreateTask &&
            message.Task is { } task &&
            transfer.TaskIds.Contains(task.Id))
        {
            transfer.FailedIds.Add(task.Id);
        }
    }

    /// <summary>
    /// After the destination account's requests ran, replaces the "Adding" notice with the result once every
    /// create was either stored or refused. A sync that ran before the creates leaves the notice as it is.
    /// </summary>
    private async Task CompleteTransferIfDoneAsync(Guid accountId)
    {
        if (_pendingTransfer is not { } transfer || transfer.AccountId != accountId)
            return;

        var added = 0;
        foreach (var taskId in transfer.TaskIds.Where(id => !transfer.FailedIds.Contains(id)))
        {
            if (await _taskService.GetTaskAsync(taskId).ConfigureAwait(false) is { PendingMutation: TaskPendingMutation.None })
                added++;
        }

        if (added + transfer.FailedIds.Count < transfer.TaskIds.Count || !ReferenceEquals(_pendingTransfer, transfer))
            return;

        _pendingTransfer = null;
        if (transfer.FailedIds.Count == 0)
            ShowNotice(transfer.Title, string.Format(Translator.ToDoPage_TransferDone, added, transfer.ListTitle), InfoBarMessageType.Success, transfer.ListId);
        else
            ShowNotice(transfer.Title, string.Format(Translator.ToDoPage_TransferPartial, added, transfer.TaskIds.Count, transfer.ListTitle), InfoBarMessageType.Warning, transfer.ListId);
    }

    /// <summary>A notice about a list that no longer exists is closed, and its transfer is no longer followed.</summary>
    private void CloseNoticesOfList(Guid listId)
    {
        if (_pendingTransfer?.ListId == listId)
            _pendingTransfer = null;

        if (_noticeListId == listId)
            IsNoticeOpen = false;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return cleaned.Length == 0 ? "Tasks" : cleaned;
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
