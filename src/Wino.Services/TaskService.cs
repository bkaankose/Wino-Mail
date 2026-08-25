using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Owns the account task cache and optimistic local mutations. Provider synchronizers
/// reconcile this cache; this service never performs network I/O.
/// </summary>
public sealed class TaskService : BaseDatabaseService, ITaskService
{
    public TaskService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<List<AccountTaskList>> GetTaskListsAsync(Guid? accountId = null)
    {
        var lists = await Connection.Table<AccountTaskList>()
            .Where(list => list.PendingMutation != TaskPendingMutation.Delete)
            .ToListAsync().ConfigureAwait(false);
        return accountId is null ? lists : lists.Where(list => list.MailAccountId == accountId.Value).ToList();
    }

    public Task<AccountTaskList> GetTaskListAsync(Guid listId)
        => Connection.Table<AccountTaskList>().FirstOrDefaultAsync(list => list.Id == listId);

    public async Task<AccountTaskList> GetOrCreateLocalTaskListAsync(Guid accountId, string displayName)
    {
        var localLists = (await Connection.Table<AccountTaskList>()
            .Where(list => list.MailAccountId == accountId && list.SourceKind == TaskSourceKind.Local)
            .ToListAsync()
            .ConfigureAwait(false))
            .Where(list => list.PendingMutation != TaskPendingMutation.Delete)
            .OrderByDescending(list => list.IsDefault)
            .ThenBy(list => list.CreatedAtUtc)
            .ThenBy(list => list.Id)
            .ToList();

        if (localLists.Count > 0)
        {
            var selected = localLists[0];
            foreach (var list in localLists)
            {
                var shouldBeDefault = list.Id == selected.Id;
                if (list.IsDefault == shouldBeDefault && (!shouldBeDefault || !list.IsReadOnly))
                    continue;

                list.IsDefault = shouldBeDefault;
                if (shouldBeDefault)
                    list.IsReadOnly = false;
                list.ModifiedAtUtc = DateTime.UtcNow;
                await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
            }

            return selected;
        }

        var createdList = new AccountTaskList
        {
            MailAccountId = accountId,
            SourceKind = TaskSourceKind.Local,
            Title = string.IsNullOrWhiteSpace(displayName) ? "Tasks" : displayName,
            IsDefault = true
        };
        await Connection.InsertAsync(createdList, typeof(AccountTaskList)).ConfigureAwait(false);
        return createdList;
    }

    public async Task<AccountTaskList> CreateTaskListAsync(Guid accountId, string title)
    {
        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(item => item.Id == accountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account does not exist.");
        if (!account.IsTaskAccessEnabled)
            throw new InvalidOperationException("Tasks are not enabled for this account.");
        var source = ResolveSource(account);
        var now = DateTime.UtcNow;
        var list = new AccountTaskList
        {
            MailAccountId = accountId,
            SourceKind = source,
            Title = string.IsNullOrWhiteSpace(title) ? "Tasks" : title.Trim(),
            IsDefault = false,
            PendingMutation = source == TaskSourceKind.Local ? TaskPendingMutation.None : TaskPendingMutation.Create,
            CreatedAtUtc = now,
            ModifiedAtUtc = now
        };
        await Connection.InsertAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
        return list;
    }

    public async Task<AccountTaskList> UpsertRemoteTaskListAsync(AccountTaskList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var existing = await Connection.Table<AccountTaskList>()
            .FirstOrDefaultAsync(item => item.MailAccountId == list.MailAccountId &&
                                         item.SourceKind == list.SourceKind &&
                                         item.RemoteId == list.RemoteId).ConfigureAwait(false);
        if (existing is not null)
        {
            list.Id = existing.Id;
            list.CreatedAtUtc = existing.CreatedAtUtc;
            list.PendingMutation = TaskPendingMutation.None;
            list.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
            return list;
        }

        list.Id = list.Id == Guid.Empty ? Guid.NewGuid() : list.Id;
        list.PendingMutation = TaskPendingMutation.None;
        list.CreatedAtUtc = DateTime.UtcNow;
        list.ModifiedAtUtc = list.CreatedAtUtc;
        await Connection.InsertAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
        return list;
    }

    public async Task CommitTaskListDeltaLinkAsync(Guid listId, string listDeltaLink)
    {
        if (string.IsNullOrWhiteSpace(listDeltaLink))
            return;

        var list = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (list is null)
            return;

        list.ListDeltaLink = listDeltaLink;
        list.LastSuccessfulSyncUtc = DateTime.UtcNow;
        await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
    }

    public async Task UpdateTaskListAsync(AccountTaskList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.Title = string.IsNullOrWhiteSpace(list.Title) ? "Tasks" : list.Title.Trim();
        list.ModifiedAtUtc = DateTime.UtcNow;
        if (list.SourceKind != TaskSourceKind.Local && list.PendingMutation == TaskPendingMutation.None)
            list.PendingMutation = TaskPendingMutation.Update;
        await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
    }

    public async Task DeleteTaskListAsync(Guid listId)
    {
        var list = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (list is null || list.IsReadOnly)
            return;
        var tasks = await Connection.Table<AccountTask>().Where(task => task.TaskListId == listId).ToListAsync().ConfigureAwait(false);
        foreach (var task in tasks)
            await DeleteTaskAsync(task.Id).ConfigureAwait(false);

        if (list.SourceKind == TaskSourceKind.Local)
        {
            await Connection.ExecuteAsync("DELETE FROM TaskList WHERE Id = ?", list.Id).ConfigureAwait(false);
        }
        else
        {
            list.PendingMutation = TaskPendingMutation.Delete;
            list.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
        }
    }

    public Task RemoveTaskListAsync(Guid listId)
        => Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM TaskStep WHERE TaskId IN (SELECT Id FROM TaskCard WHERE TaskListId = ?)", listId);
            transaction.Execute("DELETE FROM TaskCard WHERE TaskListId = ?", listId);
            transaction.Execute("DELETE FROM TaskList WHERE Id = ?", listId);
        });

    public async Task<List<AccountTask>> GetTasksAsync(Guid? accountId = null, Guid? listId = null, TaskViewKind view = TaskViewKind.All, string search = null, TaskSortKind sort = TaskSortKind.DueDate)
    {
        var today = DateTime.UtcNow.Date;
        var tasks = await Connection.Table<AccountTask>().ToListAsync().ConfigureAwait(false);
        var filtered = tasks.Where(task => task.PendingMutation != TaskPendingMutation.Delete);
        if (accountId is not null)
            filtered = filtered.Where(task => task.MailAccountId == accountId.Value);
        if (listId is not null)
            filtered = filtered.Where(task => task.TaskListId == listId.Value);

        filtered = view switch
        {
            TaskViewKind.Completed => filtered.Where(task => task.IsCompleted),
            TaskViewKind.Planned => filtered.Where(task => !task.IsCompleted && task.DueDate.HasValue),
            TaskViewKind.MyDay => filtered.Where(task => task.MyDayDateUtc == today),
            TaskViewKind.Important => filtered.Where(task => task.IsImportant),
            _ => filtered
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim();
            filtered = filtered.Where(task => (task.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                              (task.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Completed items always sink, whichever sort is active.
        var ordered = filtered.OrderBy(task => task.IsCompleted);
        ordered = sort switch
        {
            TaskSortKind.Importance => ordered.ThenByDescending(task => task.IsImportant).ThenBy(task => task.DueDate ?? DateTime.MaxValue),
            TaskSortKind.MyDay => ordered.ThenByDescending(task => task.MyDayDateUtc ?? DateTime.MinValue).ThenBy(task => task.DueDate ?? DateTime.MaxValue),
            TaskSortKind.Alphabetical => ordered.ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase),
            TaskSortKind.CreationDate => ordered.ThenByDescending(task => task.CreatedAtUtc),
            _ => ordered.ThenBy(task => task.DueDate ?? DateTime.MaxValue)
        };

        var result = ordered.ThenBy(task => task.ModifiedAtUtc).ToList();
        await LoadStepsAsync(result).ConfigureAwait(false);
        return result;
    }

    public async Task<List<AccountTask>> GetMyDaySuggestionsAsync()
    {
        var today = DateTime.UtcNow.Date;
        var yesterday = today.AddDays(-1);
        var recentThreshold = today.AddDays(-7);

        var tasks = await Connection.Table<AccountTask>().ToListAsync().ConfigureAwait(false);
        var candidates = tasks
            .Where(task => task.PendingMutation != TaskPendingMutation.Delete)
            .Where(task => !task.IsCompleted && task.MyDayDateUtc != today)
            .ToList();

        // Rank buckets rather than concatenating queries, so a task that qualifies twice
        // is suggested once under its most urgent reason.
        static int Bucket(AccountTask task, DateTime today, DateTime yesterday, DateTime recentThreshold)
        {
            if (task.DueDate is { } due && due < today)
                return 0;
            if (task.DueDate == today)
                return 1;
            if (task.MyDayDateUtc == yesterday)
                return 2;
            if (task.CreatedAtUtc >= recentThreshold)
                return 3;
            return int.MaxValue;
        }

        var result = candidates
            .Select(task => (Task: task, Rank: Bucket(task, today, yesterday, recentThreshold)))
            .Where(entry => entry.Rank != int.MaxValue)
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.Task.DueDate ?? DateTime.MaxValue)
            .ThenByDescending(entry => entry.Task.CreatedAtUtc)
            .Select(entry => entry.Task)
            .ToList();

        await LoadStepsAsync(result).ConfigureAwait(false);
        return result;
    }

    public async Task<AccountTask> GetTaskAsync(Guid taskId)
    {
        var task = await Connection.Table<AccountTask>().FirstOrDefaultAsync(item => item.Id == taskId).ConfigureAwait(false);
        if (task is not null)
            await LoadStepsAsync([task]).ConfigureAwait(false);
        return task;
    }

    public async Task<AccountTask> CreateTaskAsync(AccountTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id;
        task.Title = task.Title?.Trim() ?? string.Empty;
        task.DueDate = NormalizeDate(task.DueDate);
        task.MyDayDateUtc = NormalizeDate(task.MyDayDateUtc);
        task.CreatedAtUtc = DateTime.UtcNow;
        task.ModifiedAtUtc = task.CreatedAtUtc;
        task.PendingMutation = task.SourceKind == TaskSourceKind.Local ? TaskPendingMutation.None : TaskPendingMutation.Create;
        await Connection.InsertAsync(task, typeof(AccountTask)).ConfigureAwait(false);
        return task;
    }

    public async Task UpdateTaskAsync(AccountTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Title = task.Title?.Trim() ?? string.Empty;
        task.DueDate = NormalizeDate(task.DueDate);
        task.MyDayDateUtc = NormalizeDate(task.MyDayDateUtc);
        task.ModifiedAtUtc = DateTime.UtcNow;
        if (task.SourceKind != TaskSourceKind.Local && task.PendingMutation == TaskPendingMutation.None)
            task.PendingMutation = TaskPendingMutation.Update;
        await Connection.UpdateAsync(task, typeof(AccountTask)).ConfigureAwait(false);
    }

    public async Task DeleteTaskAsync(Guid taskId)
    {
        var task = await GetTaskAsync(taskId).ConfigureAwait(false);
        if (task is null)
            return;
        await Connection.Table<AccountTaskStep>().DeleteAsync(step => step.TaskId == taskId).ConfigureAwait(false);
        if (task.SourceKind == TaskSourceKind.Local)
        {
            await Connection.ExecuteAsync("DELETE FROM TaskCard WHERE Id = ?", task.Id).ConfigureAwait(false);
        }
        else
        {
            task.PendingMutation = TaskPendingMutation.Delete;
            task.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.UpdateAsync(task, typeof(AccountTask)).ConfigureAwait(false);
        }
    }

    public async Task<AccountTaskStep> CreateStepAsync(AccountTaskStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        step.Id = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id;
        step.ModifiedAtUtc = DateTime.UtcNow;
        step.CreatedAtUtc = step.ModifiedAtUtc;
        step.PendingMutation = step.SourceKind == TaskSourceKind.Local ? TaskPendingMutation.None : TaskPendingMutation.Create;
        await Connection.InsertAsync(step, typeof(AccountTaskStep)).ConfigureAwait(false);
        return step;
    }

    public async Task UpdateStepAsync(AccountTaskStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        step.ModifiedAtUtc = DateTime.UtcNow;
        if (step.SourceKind != TaskSourceKind.Local && step.PendingMutation == TaskPendingMutation.None)
            step.PendingMutation = TaskPendingMutation.Update;
        await Connection.UpdateAsync(step, typeof(AccountTaskStep)).ConfigureAwait(false);
    }

    public async Task DeleteStepAsync(Guid stepId)
    {
        var step = await Connection.Table<AccountTaskStep>().FirstOrDefaultAsync(item => item.Id == stepId).ConfigureAwait(false);
        if (step is null)
            return;
        if (step.SourceKind == TaskSourceKind.Local)
            await Connection.ExecuteAsync("DELETE FROM TaskStep WHERE Id = ?", step.Id).ConfigureAwait(false);
        else
        {
            step.PendingMutation = TaskPendingMutation.Delete;
            step.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.UpdateAsync(step, typeof(AccountTaskStep)).ConfigureAwait(false);
        }
    }

    public async Task CompleteListMutationAsync(Guid listId, AccountTaskList remoteList, bool deleted)
    {
        var local = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (local is null)
            return;
        if (deleted)
        {
            await DeleteTaskListLocallyAsync(local).ConfigureAwait(false);
            return;
        }
        local.RemoteId = remoteList?.RemoteId ?? local.RemoteId;
        local.RemoteVersion = remoteList?.RemoteVersion ?? local.RemoteVersion;
        if (remoteList is not null)
        {
            local.Title = remoteList.Title ?? local.Title;
            local.IsDefault = remoteList.IsDefault;
            local.IsReadOnly = remoteList.IsReadOnly;
        }
        local.PendingMutation = TaskPendingMutation.None;
        local.ModifiedAtUtc = DateTime.UtcNow;
        await Connection.UpdateAsync(local, typeof(AccountTaskList)).ConfigureAwait(false);
    }

    public async Task CompleteTaskMutationAsync(Guid taskId, AccountTask remoteTask, bool deleted)
    {
        var local = await GetTaskAsync(taskId).ConfigureAwait(false);
        if (local is null)
            return;
        if (deleted)
        {
            await Connection.Table<AccountTaskStep>().DeleteAsync(step => step.TaskId == taskId).ConfigureAwait(false);
            await Connection.ExecuteAsync("DELETE FROM TaskCard WHERE Id = ?", local.Id).ConfigureAwait(false);
            return;
        }
        local.RemoteId = remoteTask?.RemoteId ?? local.RemoteId;
        local.RemoteVersion = remoteTask?.RemoteVersion ?? local.RemoteVersion;
        if (remoteTask is not null)
        {
            local.Title = remoteTask.Title;
            local.Notes = remoteTask.Notes;
            local.DueDate = NormalizeDate(remoteTask.DueDate);
            local.IsCompleted = remoteTask.IsCompleted;
            local.CompletedAtUtc = remoteTask.CompletedAtUtc;
            local.RemoteOrder = remoteTask.RemoteOrder;

            // Only Graph carries importance. Adopting a Google response would clear a local star,
            // and My Day exists nowhere upstream, so it is never touched by a reconciliation.
            if (local.SourceKind == TaskSourceKind.Outlook)
                local.IsImportant = remoteTask.IsImportant;
        }
        local.PendingMutation = TaskPendingMutation.None;
        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Update(local, typeof(AccountTask));
            if (remoteTask is null)
                return;

            transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", local.Id);
            foreach (var remoteStep in remoteTask.Steps ?? [])
            {
                remoteStep.Id = Guid.NewGuid();
                remoteStep.TaskId = local.Id;
                remoteStep.MailAccountId = local.MailAccountId;
                remoteStep.SourceKind = local.SourceKind;
                remoteStep.PendingMutation = TaskPendingMutation.None;
                transaction.Insert(remoteStep, typeof(AccountTaskStep));
            }
        }).ConfigureAwait(false);
    }

    public async Task CompleteStepMutationAsync(Guid stepId, AccountTaskStep remoteStep, bool deleted)
    {
        var local = await Connection.Table<AccountTaskStep>().FirstOrDefaultAsync(step => step.Id == stepId).ConfigureAwait(false);
        if (local is null)
            return;
        if (deleted)
        {
            await Connection.ExecuteAsync("DELETE FROM TaskStep WHERE Id = ?", local.Id).ConfigureAwait(false);
            return;
        }
        local.RemoteId = remoteStep?.RemoteId ?? local.RemoteId;
        local.RemoteVersion = remoteStep?.RemoteVersion ?? local.RemoteVersion;
        if (remoteStep is not null)
        {
            local.Title = remoteStep.Title ?? local.Title;
            local.IsCompleted = remoteStep.IsCompleted;
            local.Order = remoteStep.Order;
        }
        local.PendingMutation = TaskPendingMutation.None;
        await Connection.UpdateAsync(local, typeof(AccountTaskStep)).ConfigureAwait(false);
    }

    public async Task ReplaceListAsync(Guid listId, IReadOnlyList<AccountTask> tasks, string taskDeltaLink, DateTime? watermarkUtc = null, string listDeltaLink = null)
    {
        var list = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (list is null)
            return;
        var existing = await Connection.Table<AccountTask>().Where(task => task.TaskListId == listId).ToListAsync().ConfigureAwait(false);
        var pendingCreates = existing
            .Where(task => string.IsNullOrWhiteSpace(task.RemoteId) && task.PendingMutation == TaskPendingMutation.Create)
            .ToList();

        // This path deletes and reinserts the list, so local-only state has to be carried
        // across by remote identity. My Day never exists upstream; importance only does on Graph.
        var localOnlyState = existing
            .Where(task => !string.IsNullOrWhiteSpace(task.RemoteId))
            .GroupBy(task => task.RemoteId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var task in existing.Except(pendingCreates))
            {
                transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", task.Id);
                transaction.Execute("DELETE FROM TaskCard WHERE Id = ?", task.Id);
            }
            foreach (var task in tasks ?? [])
            {
                task.TaskListId = listId;
                task.MailAccountId = list.MailAccountId;
                task.PendingMutation = TaskPendingMutation.None;

                if (task.RemoteId is not null && localOnlyState.TryGetValue(task.RemoteId, out var previous))
                {
                    task.MyDayDateUtc = previous.MyDayDateUtc;
                    if (task.SourceKind != TaskSourceKind.Outlook)
                        task.IsImportant = previous.IsImportant;
                }

                transaction.Insert(task, typeof(AccountTask));
                foreach (var step in task.Steps ?? [])
                {
                    step.TaskId = task.Id;
                    step.MailAccountId = list.MailAccountId;
                    step.PendingMutation = TaskPendingMutation.None;
                    transaction.Insert(step, typeof(AccountTaskStep));
                }
            }

            // A provider reconciliation can race an optimistic local create. Keep that
            // queued item until its create request receives a remote identity.
            foreach (var task in pendingCreates)
            {
                foreach (var step in task.Steps ?? [])
                {
                    step.TaskId = task.Id;
                    step.MailAccountId = list.MailAccountId;
                }
            }

            list.TaskDeltaLink = taskDeltaLink;
            if (listDeltaLink is not null)
                list.ListDeltaLink = listDeltaLink;
            list.WatermarkUtc = watermarkUtc;
            list.LastSuccessfulSyncUtc = DateTime.UtcNow;
            list.PendingMutation = TaskPendingMutation.None;
            transaction.Update(list, typeof(AccountTaskList));
        }).ConfigureAwait(false);
    }

    public async Task DeleteAccountTasksAsync(Guid accountId)
    {
        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM TaskStep WHERE MailAccountId = ?", accountId);
            transaction.Execute("DELETE FROM TaskCard WHERE MailAccountId = ?", accountId);
            transaction.Execute("DELETE FROM TaskList WHERE MailAccountId = ?", accountId);
        }).ConfigureAwait(false);
    }

    public Task DeleteTaskListsBySourceAsync(Guid accountId, TaskSourceKind sourceKind)
        => Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM TaskStep WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
            transaction.Execute("DELETE FROM TaskCard WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
            transaction.Execute("DELETE FROM TaskList WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
        });

    public async Task MarkTaskListsReadOnlyAsync(Guid accountId, TaskSourceKind sourceKind, bool isReadOnly = true)
    {
        var lists = await Connection.Table<AccountTaskList>()
            .Where(list => list.MailAccountId == accountId && list.SourceKind == sourceKind)
            .ToListAsync().ConfigureAwait(false);
        foreach (var list in lists)
        {
            list.IsReadOnly = isReadOnly;
            list.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
        }
    }

    public async Task EnsureLocalTaskListAsync(Guid accountId, string displayName)
        => await GetOrCreateLocalTaskListAsync(accountId, displayName).ConfigureAwait(false);

    private async Task DeleteTaskListLocallyAsync(AccountTaskList list)
    {
        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM TaskStep WHERE TaskId IN (SELECT Id FROM TaskCard WHERE TaskListId = ?)", list.Id);
            transaction.Execute("DELETE FROM TaskCard WHERE TaskListId = ?", list.Id);
            transaction.Execute("DELETE FROM TaskList WHERE Id = ?", list.Id);
        }).ConfigureAwait(false);
    }

    private async Task LoadStepsAsync(IReadOnlyList<AccountTask> tasks)
    {
        if (tasks.Count == 0)
            return;
        var steps = await Connection.Table<AccountTaskStep>().ToListAsync().ConfigureAwait(false);
        var readOnlyListIds = (await Connection.Table<AccountTaskList>().ToListAsync().ConfigureAwait(false))
            .Where(list => list.IsReadOnly)
            .Select(list => list.Id)
            .ToHashSet();
        var byTask = steps
            .Where(step => step.PendingMutation != TaskPendingMutation.Delete)
            .GroupBy(step => step.TaskId)
            .ToDictionary(group => group.Key, group => group.OrderBy(step => step.Order).ToList());
        foreach (var task in tasks)
        {
            task.Steps = byTask.TryGetValue(task.Id, out var taskSteps) ? taskSteps : [];
            task.IsReadOnly = readOnlyListIds.Contains(task.TaskListId);
            foreach (var step in task.Steps)
                step.IsReadOnly = readOnlyListIds.Contains(task.TaskListId);
        }
    }

    private static DateTime? NormalizeDate(DateTime? value)
        => value?.Date;

    private static TaskSourceKind ResolveSource(MailAccount account)
        => account.ProviderType switch
        {
            MailProviderType.Gmail when account.IsTaskAccessGranted => TaskSourceKind.Gmail,
            MailProviderType.Outlook when account.IsTaskAccessGranted => TaskSourceKind.Outlook,
            _ => TaskSourceKind.Local
        };
}
