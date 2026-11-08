#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Misc;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Voice;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Services.Voice;

/// <summary>
/// Task steps: the device finds the tasks a plan describes (title words, list, due window, overdue, important, My Day) in
/// its own lists and changes them with the same task requests the To Do page queues. An action meant for one task that
/// matches several changes nothing unless one title matches exactly; the bubble says which ones matched.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    private const int ListedItems = 5;

    private async Task<StepOutcome> ResolveTaskStepAsync(VoicePlanPreviewGroup group, VoicePlanPreviewStep step, ResolveState state, CancellationToken cancellationToken)
    {
        var command = step.Command;
        if (command.UsesResultOf is int source)
        {
            if (!state.TaskTargets.TryGetValue(source - 1, out var found) || found.Count == 0)
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ChainedTasksSkipped, PlainText(step)));

            var chained = FilterTasksForAction(command, found, out var chainedIssue);
            if (chained.Count == 0)
                return StepOutcome.Drop(chainedIssue ?? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTasks, PlainText(step)));

            state.TaskTargets[step.CommandIndex] = chained;
            return StepOutcome.Keep(DescribeTaskTargets(chained, step.Detail));
        }

        var accounts = GroupAccounts(group);
        if (accounts.Count == 0)
            return StepOutcome.Drop(Translator.VoiceIssue_AccountUnnamed);

        var selection = command.Tasks ?? new TaskSelection();
        var lists = (await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [])
            .Where(list => accounts.Exists(account => account.Id == list.MailAccountId))
            .ToList();

        HashSet<Guid>? listIds = null;
        if (!string.IsNullOrWhiteSpace(selection.ListName))
        {
            var named = MatchTaskLists(lists, selection.ListName);
            if (named.Count == 0)
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTaskList, selection.ListName.Trim()));

            listIds = named.Select(static list => list.Id).ToHashSet();
        }

        var tasks = new List<AccountTask>();
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tasks.AddRange(await taskService.GetTasksAsync(account.Id).ConfigureAwait(false) ?? []);
        }

        var today = DateTime.Today;
        var utcToday = DateTime.UtcNow.Date;
        IEnumerable<AccountTask> rows = tasks.DistinctBy(static task => task.Id);
        if (listIds is not null)
            rows = rows.Where(task => listIds.Contains(task.TaskListId));
        if (!string.IsNullOrWhiteSpace(selection.Title))
        {
            var title = selection.Title.Trim();
            rows = rows.Where(task => Fits(task.Title, title) || Fits(title, task.Title));
        }

        if (selection.DueRange is { } range)
        {
            var from = range.StartLocal.Date;
            var until = range.EndLocal > range.StartLocal ? range.EndLocal : from.AddDays(1);
            rows = rows.Where(task => task.DueDate is { } due && due.Date >= from && due.Date < until);
        }

        if (selection.Overdue)
            rows = rows.Where(task => task.DueDate is { } due && due.Date < today && !task.IsCompleted);
        if (selection.IsImportant is bool important)
            rows = rows.Where(task => task.IsImportant == important);
        if (selection.InMyDay is bool inMyDay)
            rows = rows.Where(task => (task.MyDayDateUtc?.Date == utcToday) == inMyDay);

        rows = command.Action switch
        {
            VoiceAction.ReopenTask => rows.Where(static task => task.IsCompleted),
            VoiceAction.CompleteTask => rows.Where(static task => !task.IsCompleted),
            VoiceAction.DeleteTask => rows,
            _ when !selection.IncludeCompleted => rows.Where(static task => !task.IsCompleted),
            _ => rows
        };

        var ordered = rows
            .OrderBy(static task => task.IsCompleted)
            .ThenBy(static task => task.DueDate ?? DateTime.MaxValue)
            .ThenBy(static task => task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (selection.Limit > 0)
            ordered = ordered.Take(selection.Limit).ToList();

        if (ordered.Count == 0)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTasks, PlainText(step)));

        var isMany = selection.All || selection.Limit > 1 || command.Action == VoiceAction.FindTasks;
        if (!isMany && ordered.Count > 1)
        {
            // One task was meant: a title that matches exactly wins, otherwise nothing changes until the user says which.
            var exact = selection.Title is { Length: > 0 } wanted ? ordered.Where(task => SameText(task.Title, wanted)).ToList() : [];
            if (exact.Count != 1)
            {
                var names = ordered.Take(3).Select(static task => $"“{TaskTitle(task)}”");
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_WhichTask, ordered.Count, string.Join(", ", names)));
            }

            ordered = exact;
        }

        var targets = ordered
            .Select(task => new VoiceTaskTarget(task, lists.FirstOrDefault(list => list.Id == task.TaskListId), accounts.First(account => account.Id == task.MailAccountId)))
            .ToList();
        var runnable = FilterTasksForAction(command, targets, out var issue);
        if (runnable.Count == 0)
            return StepOutcome.Drop(issue ?? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTasks, PlainText(step)));

        state.TaskTargets[step.CommandIndex] = runnable;
        return StepOutcome.Keep(DescribeTaskTargets(runnable, step.Detail));
    }

    /// <summary>Drops what a change can't touch: tasks in read-only lists, and for a reopen or complete, tasks already so.</summary>
    private static List<VoiceTaskTarget> FilterTasksForAction(VoiceCommand command, IReadOnlyList<VoiceTaskTarget> targets, out string? issue)
    {
        issue = null;
        if (command.Action == VoiceAction.FindTasks)
            return targets.ToList();

        var writable = targets.Where(static target => !target.Task.IsReadOnly && target.List?.IsReadOnly != true).ToList();
        if (writable.Count == 0 && targets.Count > 0)
            issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ReadOnlyTask, TaskTitle(targets[0].Task));

        return writable;
    }

    private static List<AccountTaskList> MatchTaskLists(IEnumerable<AccountTaskList> lists, string name)
    {
        var all = lists.ToList();
        var exact = all.Where(list => SameText(list.Title, name)).ToList();
        return exact.Count > 0 ? exact : all.Where(list => Fits(list.Title, name) || Fits(name, list.Title)).ToList();
    }

    private static string DescribeTaskTargets(IReadOnlyList<VoiceTaskTarget> targets, string? detail)
    {
        if (targets.Count == 1)
        {
            var only = targets[0];
            return string.Join(" · ", new[] { TaskTitle(only.Task), only.List?.Title, detail }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        }

        var found = string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_TasksFound, targets.Count);
        return string.IsNullOrWhiteSpace(detail) ? found : $"{found} · {detail}";
    }

    private static string TaskTitle(AccountTask task)
        => string.IsNullOrWhiteSpace(task.Title) ? Translator.VoiceResult_NoSubject : task.Title.Replace("**", string.Empty).Trim();

    private static string TasksText(int count)
        => count == 1 ? Translator.VoiceCount_Task : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Tasks, count);

    private static string TaskName(IReadOnlyList<VoiceTaskTarget> targets)
        => targets.Count == 1 ? TaskTitle(targets[0].Task) : TasksText(targets.Count);

    private async Task<StepOutcome> ResolveTaskListStepAsync(VoicePlanPreviewGroup group, VoicePlanPreviewStep step, ResolveState state)
    {
        var command = step.Command;
        if (command.Action == VoiceAction.CreateTaskList)
        {
            if (string.IsNullOrWhiteSpace(command.TaskChange?.NewTitle))
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Title));
            if (ResolveAccount(group) is not { } account)
                return StepOutcome.Drop(Translator.VoiceIssue_AccountUnnamed);

            state.TaskListTargets[step.CommandIndex] = new List<VoiceTaskListTarget> { new(null, account) };
            return StepOutcome.Keep(step.Detail);
        }

        if (string.IsNullOrWhiteSpace(command.Tasks?.ListName))
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_TaskList));
        if (command.Action == VoiceAction.RenameTaskList && string.IsNullOrWhiteSpace(command.TaskChange?.NewTitle))
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Title));

        var accounts = GroupAccounts(group);
        var name = command.Tasks.ListName.Trim();
        var lists = MatchTaskLists((await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? []).Where(list => accounts.Exists(account => account.Id == list.MailAccountId)), name);
        if (lists.Count == 0)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTaskList, name));
        if (lists.Count > 1)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_WhichTaskList, lists.Count, name));

        var target = lists[0];
        if (target.IsReadOnly)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ReadOnlyTaskList, target.Title));
        if (command.Action == VoiceAction.DeleteTaskList && target.IsOutlookDefaultList)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_DefaultTaskList, target.Title));

        state.TaskListTargets[step.CommandIndex] = new List<VoiceTaskListTarget> { new(target, accounts.First(account => account.Id == target.MailAccountId)) };
        return StepOutcome.Keep(step.Detail);
    }

    /// <summary>The second confirmation for deleting tasks, task lists, contacts and folders and for emptying a folder.</summary>
    private static bool AddItemRisks(VoicePlanResolution resolution, VoicePlanPreviewStep step, List<VoicePlanRisk> risks)
    {
        var index = step.CommandIndex;
        switch (step.Command.Action)
        {
            case VoiceAction.DeleteTask:
                if (resolution.TaskTargets.TryGetValue(index, out var tasks) && tasks.Count > 0)
                {
                    risks.Add(new VoicePlanRisk(index,
                        string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteItem, TaskName(tasks)),
                        false,
                        string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteButton, TasksText(tasks.Count))));
                }

                return true;

            case VoiceAction.DeleteTaskList:
                if (resolution.TaskListTargets.TryGetValue(index, out var lists) && lists.FirstOrDefault()?.List is { } list)
                {
                    risks.Add(new VoicePlanRisk(index,
                        string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteTaskList, list.Title),
                        false,
                        Translator.VoiceGuard_DeleteListButton));
                }

                return true;

            case VoiceAction.DeleteContact:
                if (resolution.ContactTargets.TryGetValue(index, out var contacts) && contacts.Count > 0)
                {
                    risks.Add(new VoicePlanRisk(index,
                        string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteItem, ContactName(contacts)),
                        false,
                        string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteButton, ContactsText(contacts.Count))));
                }

                return true;

            case VoiceAction.DeleteFolder or VoiceAction.EmptyFolder:
                if (resolution.FolderTargets.TryGetValue(index, out var folders) && folders.Count > 0)
                {
                    var isEmpty = step.Command.Action == VoiceAction.EmptyFolder;
                    var names = string.Join(", ", folders.Select(static target => target.Folder?.ResolvedDisplayName).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase));
                    var accounts = AccountsLabel(folders.Select(static target => target.Account).DistinctBy(static account => account.Id).ToList());
                    var isPermanent = !isEmpty || folders.Any(static target => target.Folder?.SpecialFolderType is SpecialFolderType.Deleted or SpecialFolderType.Junk);
                    risks.Add(new VoicePlanRisk(index,
                        string.Format(CultureInfo.CurrentCulture, isEmpty ? Translator.VoiceGuard_EmptyWholeFolder : Translator.VoiceGuard_DeleteFolder, names, accounts),
                        isPermanent,
                        isEmpty ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_EmptyButton, names) : Translator.VoiceGuard_DeleteFolderButton));
                }

                return true;
        }

        return false;
    }

    private async Task<StepResult> RunTaskStepAsync(StepContext context)
    {
        var command = context.Step.Command;
        if (!context.Resolution.TaskTargets.TryGetValue(context.Step.CommandIndex, out var targets) || targets.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoTasksLeft);

        switch (command.Action)
        {
            case VoiceAction.FindTasks:
            {
                var names = string.Join(", ", targets.Take(ListedItems).Select(static target => TaskTitle(target.Task)));
                if (targets.Count > ListedItems)
                    names += string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_AndMore, targets.Count - ListedItems);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TasksFound, TasksText(targets.Count), names));
            }

            case VoiceAction.CompleteTask or VoiceAction.ReopenTask:
            {
                var completing = command.Action == VoiceAction.CompleteTask;
                var changing = targets.Where(target => target.Task.IsCompleted != completing).ToList();
                await QueueTaskUpdatesAsync(changing, task =>
                {
                    task.IsCompleted = completing;
                    task.CompletedAtUtc = completing ? DateTime.UtcNow : null;
                }).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, completing ? Translator.VoiceResult_TaskCompleted : Translator.VoiceResult_TaskReopened, TaskName(targets)));
            }

            case VoiceAction.UpdateTask:
                return await UpdateTasksAsync(command, targets).ConfigureAwait(false);

            case VoiceAction.AddTaskStep:
            {
                var text = command.TaskChange?.Step?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return StepResult.Failed(Translator.VoiceIssue_Missing_Change);

                var requests = targets.Select(target => (IRequestBase)new TaskActionRequest(
                    target.Task.MailAccountId,
                    TaskSynchronizerOperation.CreateStep,
                    Task: target.Task,
                    Step: new AccountTaskStep
                    {
                        TaskId = target.Task.Id,
                        MailAccountId = target.Task.MailAccountId,
                        SourceKind = target.Task.SourceKind,
                        Title = text,
                        Order = target.Task.Steps?.Count ?? 0
                    })
                {
                    Trace = new RequestTrace(TraceSource, Guid.NewGuid())
                }).ToList();
                await QueueTaskRequestsAsync(requests).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_StepAdded, text, TaskName(targets)));
            }

            case VoiceAction.DeleteTask:
            {
                var requests = targets.Select(target => (IRequestBase)new TaskActionRequest(
                    target.Task.MailAccountId,
                    TaskSynchronizerOperation.DeleteTask,
                    Task: target.Task,
                    OriginalTask: target.Task)
                {
                    Trace = new RequestTrace(TraceSource, Guid.NewGuid())
                }).ToList();
                await QueueTaskRequestsAsync(requests).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, TaskName(targets)));
            }
        }

        return StepResult.None;
    }

    private async Task<StepResult> UpdateTasksAsync(VoiceCommand command, IReadOnlyList<VoiceTaskTarget> targets)
    {
        if (command.TaskChange is not { } change
            || (string.IsNullOrWhiteSpace(change.NewTitle) && change.Notes is null && change.DueDate is null && !change.ClearDueDate
                && change.IsImportant is null && change.InMyDay is null && string.IsNullOrWhiteSpace(change.ListName)))
            return StepResult.Failed(Translator.VoiceIssue_Missing_Change);

        var destinations = new Dictionary<Guid, AccountTaskList>();
        if (!string.IsNullOrWhiteSpace(change.ListName))
        {
            var lists = await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
            foreach (var target in targets)
            {
                // A task moves only to a writable list of its own account and store, as the To Do page allows.
                var candidates = lists.Where(list => !list.IsReadOnly && list.MailAccountId == target.Task.MailAccountId && list.SourceKind == target.Task.SourceKind);
                if (MatchTaskLists(candidates, change.ListName).FirstOrDefault() is { } destination)
                    destinations[target.Task.Id] = destination;
            }

            if (destinations.Count == 0)
                return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTaskList, change.ListName.Trim()));
        }

        await QueueTaskUpdatesAsync(targets, task =>
        {
            if (!string.IsNullOrWhiteSpace(change.NewTitle))
                task.Title = change.NewTitle.Trim();
            if (change.Notes is not null)
                task.Notes = change.Notes;
            if (change.DueDate is { } due)
                task.DueDate = due.ToDateTime(TimeOnly.MinValue);
            else if (change.ClearDueDate)
                task.DueDate = null;
            if (change.IsImportant is bool important)
                task.IsImportant = important;
            if (change.InMyDay is bool inMyDay)
                task.MyDayDateUtc = inMyDay ? DateTime.UtcNow.Date : null;
            if (destinations.TryGetValue(task.Id, out var destination))
                task.TaskListId = destination.Id;
        }).ConfigureAwait(false);

        return StepResult.Done(change.DueDate is { } newDue && targets.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TaskDue, TaskName(targets), newDue.ToString("D", CultureInfo.CurrentCulture))
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventUpdated, TaskName(targets)));
    }

    /// <summary>Queues an update per task the way the To Do page does: the desired copy with the original beside it.</summary>
    private Task QueueTaskUpdatesAsync(IEnumerable<VoiceTaskTarget> targets, Action<AccountTask> change)
    {
        var requests = new List<IRequestBase>();
        foreach (var target in targets)
        {
            var original = RequestEntityCloner.Task(target.Task);
            var desired = RequestEntityCloner.Task(target.Task);
            change(desired);
            requests.Add(new TaskActionRequest(desired.MailAccountId, TaskSynchronizerOperation.UpdateTask, Task: desired, OriginalTask: original)
            {
                Trace = new RequestTrace(TraceSource, Guid.NewGuid())
            });
        }

        return QueueTaskRequestsAsync(requests);
    }

    private async Task QueueTaskRequestsAsync(IReadOnlyList<IRequestBase> requests)
    {
        foreach (var accountRequests in requests.OfType<TaskActionRequest>().GroupBy(static request => request.MailAccountId))
            await requestDelegator.ExecuteAsync(accountRequests.Key, accountRequests.Cast<IRequestBase>().ToArray()).ConfigureAwait(false);
    }

    private async Task<StepResult> RunTaskListStepAsync(StepContext context)
    {
        var command = context.Step.Command;
        if (!context.Resolution.TaskListTargets.TryGetValue(context.Step.CommandIndex, out var targets) || targets.FirstOrDefault() is not { } target)
            return StepResult.Failed(Translator.VoiceResult_NoTasksLeft);

        switch (command.Action)
        {
            case VoiceAction.CreateTaskList when command.TaskChange?.NewTitle is { Length: > 0 } title:
            {
                var account = target.Account;
                var existing = await taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false) ?? [];
                var now = DateTime.UtcNow;
                var list = new AccountTaskList
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = account.Id,
                    SourceKind = ResolveTaskSource(account),
                    Title = title.Trim(),
                    ColorHex = ColorPalette.GetDistinctColor(existing.Select(static list => list.ColorHex)),
                    SortOrder = existing.Count(static list => list.GroupId is null),
                    CreatedAtUtc = now,
                    ModifiedAtUtc = now
                };

                await requestDelegator.ExecuteAsync(account.Id, (IRequestBase[])[new TaskActionRequest(account.Id, TaskSynchronizerOperation.CreateList, List: list) { Trace = new RequestTrace(TraceSource, Guid.NewGuid()) }]).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TaskListCreated, list.Title));
            }

            case VoiceAction.RenameTaskList when target.List is { } list && command.TaskChange?.NewTitle is { Length: > 0 } newTitle:
            {
                var original = RequestEntityCloner.TaskList(list);
                var desired = RequestEntityCloner.TaskList(list);
                desired.Title = newTitle.Trim();
                await requestDelegator.ExecuteAsync(list.MailAccountId, (IRequestBase[])[new TaskActionRequest(list.MailAccountId, TaskSynchronizerOperation.UpdateList, List: desired, OriginalList: original) { Trace = new RequestTrace(TraceSource, Guid.NewGuid()) }]).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Renamed, list.Title, desired.Title));
            }

            case VoiceAction.DeleteTaskList when target.List is { } list:
                await requestDelegator.ExecuteAsync(list.MailAccountId, (IRequestBase[])[new TaskActionRequest(list.MailAccountId, TaskSynchronizerOperation.DeleteList, List: list, OriginalList: list) { Trace = new RequestTrace(TraceSource, Guid.NewGuid()) }]).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, list.Title));
        }

        return StepResult.Failed(Translator.VoiceIssue_Missing_Change);
    }

    private static TaskSourceKind ResolveTaskSource(MailAccount account)
        => account.TaskIntegrationSource == AccountIntegrationSource.Provider && account.IsTaskAccessGranted
            ? account.ProviderType switch
            {
                MailProviderType.Gmail => TaskSourceKind.Gmail,
                MailProviderType.Outlook => TaskSourceKind.Outlook,
                _ => TaskSourceKind.Local
            }
            : TaskSourceKind.Local;
}
