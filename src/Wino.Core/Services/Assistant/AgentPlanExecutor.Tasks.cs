#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Misc;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// Task steps: the device reads the tasks a <see cref="TaskQuery"/> describes (list ids, title words, a due window,
/// overdue, important, My Day) in its own lists, or the exact tasks a selection names, and changes them with the same
/// task requests the To Do page queues. Task lists are created, renamed and deleted the same way.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    // ---- targets -------------------------------------------------------------------------------------------------

    private async Task<List<TaskTarget>> ResolveTasksAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (run.Tasks.TryGetValue(step.N, out var cached))
            return cached;

        List<TaskTarget> targets;
        switch (step.Target)
        {
            case StepTarget.Tasks query:
                targets = await FindTasksAsync(query.AccountId, query.Query, step.Action, run, ct).ConfigureAwait(false);
                break;

            case StepTarget.TaskItems items:
                targets = await LoadTasksAsync(items.Items, run, ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep { Part: "items" } from:
                targets = run.Tasks.TryGetValue(from.N, out var source)
                    ? source.ToList()
                    : await LoadTasksAsync(TouchedOf(run, from.N, ItemKind.Task), run, ct).ConfigureAwait(false);
                break;

            default:
                targets = [];
                break;
        }

        run.Tasks[step.N] = targets;
        return targets;
    }

    private async Task<List<TaskTarget>> LoadTasksAsync(IEnumerable<ItemRef> items, PlanRun run, CancellationToken ct)
    {
        var lists = await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
        var targets = new List<TaskTarget>();
        foreach (var item in items.Where(static item => item.Kind == ItemKind.Task))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(item.Id, out var id))
                continue;

            try
            {
                var task = await taskService.GetTaskAsync(id).ConfigureAwait(false);
                if (task is null || await AccountAsync(run, task.MailAccountId).ConfigureAwait(false) is not { } account)
                    continue;

                targets.Add(new TaskTarget(task, lists.FirstOrDefault(list => list.Id == task.TaskListId), account));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Could not load a task of an assistant step.");
            }
        }

        return targets.DistinctBy(static target => target.Task.Id).ToList();
    }

    /// <summary>
    /// The tasks a query matches in the account(s): in the named lists, by title words, due window, overdue, important
    /// and My Day; open tasks unless completed ones are asked for (a reopen reads completed ones, a delete all of them).
    /// </summary>
    private async Task<List<TaskTarget>> FindTasksAsync(Guid? accountId, TaskQuery query, AgentAction action, PlanRun run, CancellationToken ct)
    {
        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        if (accountId is Guid one)
            accounts = accounts.Where(account => account.Id == one).ToList();

        var lists = (await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [])
            .Where(list => accounts.Exists(account => account.Id == list.MailAccountId))
            .ToList();
        var listIds = query.ListIds.Count > 0 ? query.ListIds.ToHashSet() : null;

        var tasks = new List<AccountTask>();
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            tasks.AddRange(await taskService.GetTasksAsync(account.Id).ConfigureAwait(false) ?? []);
        }

        var today = DateTime.Today;
        var utcToday = DateTime.UtcNow.Date;
        IEnumerable<AccountTask> rows = tasks.DistinctBy(static task => task.Id);
        if (listIds is not null)
            rows = rows.Where(task => listIds.Contains(task.TaskListId));
        if (!string.IsNullOrWhiteSpace(query.TitleWords))
        {
            var title = query.TitleWords.Trim();
            rows = rows.Where(task => Fits(task.Title, title) || Fits(title, task.Title) || FitsWords(title, task.Title));
        }

        if (query.DueFrom is { } from)
            rows = rows.Where(task => task.DueDate is { } due && due.Date >= from.Date);
        if (query.DueTo is { } until)
            rows = rows.Where(task => task.DueDate is { } due && due.Date <= until.Date);
        if (query.Overdue)
            rows = rows.Where(task => task.DueDate is { } due && due.Date < today && !task.IsCompleted);
        if (query.Important)
            rows = rows.Where(static task => task.IsImportant);
        if (query.MyDay)
            rows = rows.Where(task => task.MyDayDateUtc?.Date == utcToday);

        rows = action switch
        {
            AgentAction.ReopenTask => rows.Where(static task => task.IsCompleted),
            AgentAction.CompleteTask => rows.Where(static task => !task.IsCompleted),
            AgentAction.DeleteTask => rows,
            _ when !query.IncludeCompleted => rows.Where(static task => !task.IsCompleted),
            _ => rows
        };

        var ordered = rows
            .OrderBy(static task => task.IsCompleted)
            .ThenBy(static task => task.DueDate ?? DateTime.MaxValue)
            .ThenBy(static task => task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (query.Limit > 0)
            ordered = ordered.Take(query.Limit).ToList();

        return ordered
            .Select(task => new TaskTarget(task, lists.FirstOrDefault(list => list.Id == task.TaskListId), accounts.First(account => account.Id == task.MailAccountId)))
            .ToList();
    }

    /// <summary>Drops what a change cannot touch: tasks in read-only lists.</summary>
    private static List<TaskTarget> FilterTasksForAction(AgentAction action, IReadOnlyList<TaskTarget> targets, out string? issue)
    {
        issue = null;
        if (action == AgentAction.FindTasks)
            return targets.ToList();

        var writable = targets.Where(static target => !target.Task.IsReadOnly && target.List?.IsReadOnly != true).ToList();
        if (writable.Count == 0 && targets.Count > 0)
            issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ReadOnlyTask, TaskTitle(targets[0].Task));

        return writable;
    }

    private static ItemRef Ref(TaskTarget target)
        => new(ItemKind.Task, target.Task.MailAccountId, target.Task.Id.ToString());

    private static List<ItemRef> Refs(IEnumerable<TaskTarget> targets) => targets.Select(Ref).ToList();

    private static string TaskTitle(AccountTask task)
        => string.IsNullOrWhiteSpace(task.Title) ? Translator.VoiceResult_NoSubject : Plain(task.Title);

    private static string TaskName(IReadOnlyList<TaskTarget> targets)
        => targets.Count == 1 ? TaskTitle(targets[0].Task) : TasksText(targets.Count);

    // ---- runners -------------------------------------------------------------------------------------------------

    private async Task<StepOutcome> RunTaskStepAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var found = await ResolveTasksAsync(step, run, ct).ConfigureAwait(false);
        if (found.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoTasks);

        var targets = FilterTasksForAction(step.Action, found, out var issue);
        if (targets.Count == 0)
            return Failed(step, issue ?? Translator.VoiceResult_NoTasksLeft);

        var change = step.Args as StepArgs.TaskChangeArgs;
        switch (step.Action)
        {
            case AgentAction.FindTasks:
            {
                var names = string.Join(", ", targets.Take(ListedItems).Select(static target => TaskTitle(target.Task)));
                if (targets.Count > ListedItems)
                    names += string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_AndMore, targets.Count - ListedItems);
                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TasksFound, TasksText(targets.Count), names));
            }

            case AgentAction.CompleteTask or AgentAction.ReopenTask:
            {
                var completing = step.Action == AgentAction.CompleteTask;
                var changing = targets.Where(target => target.Task.IsCompleted != completing).ToList();
                await QueueTaskUpdatesAsync(changing, task =>
                {
                    task.IsCompleted = completing;
                    task.CompletedAtUtc = completing ? DateTime.UtcNow : null;
                }).ConfigureAwait(false);
                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, completing ? Translator.VoiceResult_TaskCompleted : Translator.VoiceResult_TaskReopened, TaskName(targets)));
            }

            case AgentAction.UpdateTask:
                return await UpdateTasksAsync(step, change, targets).ConfigureAwait(false);

            case AgentAction.AddTaskStep:
            {
                var text = change?.Step?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return Failed(step, Translator.VoiceIssue_Missing_Change);

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
                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_StepAdded, Plain(text), TaskName(targets)));
            }

            case AgentAction.DeleteTask:
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
                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, TaskName(targets)));
            }
        }

        return Failed(step, Translator.VoiceIssue_Missing_Change);
    }

    private async Task<StepOutcome> UpdateTasksAsync(ResolvedStep step, StepArgs.TaskChangeArgs? change, IReadOnlyList<TaskTarget> targets)
    {
        if (change is null
            || (string.IsNullOrWhiteSpace(change.NewTitle) && change.Notes is null && change.NewDue is null && !change.ClearDue
                && change.SetImportant is null && change.SetMyDay is null && change.ToListId is null))
            return Failed(step, Translator.VoiceIssue_Missing_Change);

        AccountTaskList? destination = null;
        if (change.ToListId is Guid listId)
        {
            destination = await taskService.GetTaskListAsync(listId).ConfigureAwait(false);
            if (destination is null || destination.IsReadOnly)
                return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_TaskList));
        }

        // A task moves only to a writable list of its own account and store, as the To Do page allows.
        var moving = destination is null
            ? targets
            : targets.Where(target => target.Task.MailAccountId == destination.MailAccountId && target.Task.SourceKind == destination.SourceKind).ToList();
        if (moving.Count == 0)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoTaskList, Plain(destination?.Title ?? string.Empty)));

        await QueueTaskUpdatesAsync(moving, task =>
        {
            if (!string.IsNullOrWhiteSpace(change.NewTitle))
                task.Title = change.NewTitle.Trim();
            if (change.Notes is not null)
                task.Notes = change.Notes;
            if (change.NewDue is { } due)
                task.DueDate = due.Date;
            else if (change.ClearDue)
                task.DueDate = null;
            if (change.SetImportant is bool important)
                task.IsImportant = important;
            if (change.SetMyDay is bool inMyDay)
                task.MyDayDateUtc = inMyDay ? DateTime.UtcNow.Date : null;
            if (destination is not null)
                task.TaskListId = destination.Id;
        }).ConfigureAwait(false);

        return Done(step, Refs(moving), change.NewDue is { } newDue && moving.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TaskDue, TaskName(moving), newDue.ToString("D", CultureInfo.CurrentCulture))
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventUpdated, TaskName(moving)));
    }

    /// <summary>Queues an update per task the way the To Do page does: the desired copy with the original beside it.</summary>
    private Task QueueTaskUpdatesAsync(IEnumerable<TaskTarget> targets, Action<AccountTask> change)
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

    // ---- creation ------------------------------------------------------------------------------------------------

    /// <summary>Adds a task to the named list, else the last used, default or first writable one; created directly like the To Do page's quick add.</summary>
    private async Task<StepOutcome> CreateTaskAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (step.Args is not StepArgs.TaskArgs details)
            return Failed(step, Translator.VoiceIssue_MissingDetails);
        if (string.IsNullOrWhiteSpace(details.Title))
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Title));

        var lists = await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
        var writable = lists.Where(static list => !list.IsReadOnly).ToList();
        var destination = (details.ListId is Guid listId ? writable.FirstOrDefault(list => list.Id == listId) : null)
            ?? writable.FirstOrDefault(list => list.Id == preferencesService.LastUsedTaskListId)
            ?? writable.FirstOrDefault(static list => list.IsDefault)
            ?? writable.FirstOrDefault();

        if (destination is null)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoTaskList, Translator.VoiceBubble_AllAccounts));

        var task = new AccountTask
        {
            Id = Guid.NewGuid(),
            MailAccountId = destination.MailAccountId,
            TaskListId = destination.Id,
            SourceKind = destination.SourceKind,
            Title = details.Title.Trim(),
            Notes = details.Notes ?? string.Empty,
            DueDate = details.Due?.Date,
            IsImportant = details.Important,
            MyDayDateUtc = details.MyDay ? DateTime.UtcNow.Date : null
        };

        var request = new TaskActionRequest(destination.MailAccountId, TaskSynchronizerOperation.CreateTask, Task: task)
        {
            Trace = new RequestTrace(TraceSource, Guid.NewGuid())
        };

        await requestDelegator.ExecuteAsync(destination.MailAccountId, (IRequestBase[])[request]).ConfigureAwait(false);
        var account = await AccountAsync(run, destination.MailAccountId).ConfigureAwait(false);
        run.Tasks[step.N] = account is null ? [] : [new TaskTarget(task, destination, account)];
        return Done(step, [new ItemRef(ItemKind.Task, task.MailAccountId, task.Id.ToString())], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TaskCreated, Plain(task.Title)));
    }

    // ---- task lists ----------------------------------------------------------------------------------------------

    private async Task<StepOutcome> RunTaskListStepAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var args = step.Args as StepArgs.FolderArgs;
        if (step.Action == AgentAction.CreateTaskList)
        {
            var title = args?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(title))
                return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Title));

            var accounts = await AccountsAsync(run).ConfigureAwait(false);
            var account = await AccountAsync(run, args?.AccountId).ConfigureAwait(false) ?? (accounts.Count == 1 ? accounts[0] : null);
            if (account is null)
                return Failed(step, Translator.VoiceIssue_AccountUnnamed);

            var existing = await taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false) ?? [];
            var now = DateTime.UtcNow;
            var list = new AccountTaskList
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                SourceKind = ResolveTaskSource(account),
                Title = title,
                ColorHex = ColorPalette.GetDistinctColor(existing.Select(static list => list.ColorHex)),
                SortOrder = existing.Count(static list => list.GroupId is null),
                CreatedAtUtc = now,
                ModifiedAtUtc = now
            };

            await requestDelegator.ExecuteAsync(account.Id, (IRequestBase[])[new TaskActionRequest(account.Id, TaskSynchronizerOperation.CreateList, List: list) { Trace = new RequestTrace(TraceSource, Guid.NewGuid()) }]).ConfigureAwait(false);
            return Done(step, [ItemRef.TaskList(account.Id, list.Id)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TaskListCreated, Plain(list.Title)));
        }

        if (step.Target is not StepTarget.TaskListTarget target)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_TaskList));

        var existingList = await taskService.GetTaskListAsync(target.ListId).ConfigureAwait(false);
        if (existingList is null)
            return NothingMatched(step, Translator.VoiceIssue_NoTaskList);
        if (existingList.IsReadOnly)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ReadOnlyTaskList, Plain(existingList.Title ?? string.Empty)));

        switch (step.Action)
        {
            case AgentAction.RenameTaskList:
            {
                var newTitle = args?.NewName?.Trim() ?? args?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(newTitle))
                    return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Title));

                var original = RequestEntityCloner.TaskList(existingList);
                var desired = RequestEntityCloner.TaskList(existingList);
                desired.Title = newTitle;
                await requestDelegator.ExecuteAsync(existingList.MailAccountId, (IRequestBase[])[new TaskActionRequest(existingList.MailAccountId, TaskSynchronizerOperation.UpdateList, List: desired, OriginalList: original) { Trace = new RequestTrace(TraceSource, Guid.NewGuid()) }]).ConfigureAwait(false);
                return Done(step, [ItemRef.TaskList(existingList.MailAccountId, existingList.Id)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Renamed, Plain(existingList.Title ?? string.Empty), Plain(newTitle)));
            }

            case AgentAction.DeleteTaskList:
                if (existingList.IsOutlookDefaultList)
                    return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_DefaultTaskList, Plain(existingList.Title ?? string.Empty)));

                if (existingList.IsSharedCalendarCollection)
                    return Failed(step, Translator.CalDavTasks_SharedCollectionDeleteBlocked);

                await requestDelegator.ExecuteAsync(existingList.MailAccountId, (IRequestBase[])[new TaskActionRequest(existingList.MailAccountId, TaskSynchronizerOperation.DeleteList, List: existingList, OriginalList: existingList) { Trace = new RequestTrace(TraceSource, Guid.NewGuid()) }]).ConfigureAwait(false);
                return Done(step, [ItemRef.TaskList(existingList.MailAccountId, existingList.Id)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, Plain(existingList.Title ?? string.Empty)));
        }

        return Failed(step, Translator.VoiceIssue_Missing_Change);
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
