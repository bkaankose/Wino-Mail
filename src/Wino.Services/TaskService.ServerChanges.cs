using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Reports what a synchronizer stored, so open task surfaces follow the server without a reload.
/// Each apply records only the rows its delta touched, using the rows it already loaded.
/// </summary>
public sealed partial class TaskService
{
    // An initial import or a removed list can touch hundreds of tasks; one reload is cheaper
    // than that many UI-thread round trips.
    private const int ServerTaskMessageLimit = 50;

    private sealed class ServerTaskChanges(Guid accountId)
    {
        private readonly Dictionary<Guid, AccountTaskListGroup> _groups = [];
        private readonly Dictionary<Guid, AccountTaskList> _lists = [];
        private readonly Dictionary<Guid, AccountTask> _tasks = [];
        private readonly Dictionary<Guid, AccountTaskListGroup> _deletedGroups = [];
        private readonly Dictionary<Guid, AccountTaskList> _deletedLists = [];
        private readonly Dictionary<Guid, AccountTask> _deletedTasks = [];

        public Guid AccountId { get; } = accountId;
        public IEnumerable<AccountTaskListGroup> Groups => _groups.Values;
        public IEnumerable<AccountTaskList> Lists => _lists.Values;
        public IEnumerable<AccountTask> Tasks => _tasks.Values;
        public IEnumerable<AccountTaskListGroup> DeletedGroups => _deletedGroups.Values;
        public IEnumerable<AccountTaskList> DeletedLists => _deletedLists.Values;
        public IEnumerable<AccountTask> DeletedTasks => _deletedTasks.Values;
        public int TaskCount => _tasks.Count + _deletedTasks.Count;
        public bool IsEmpty => _groups.Count + _lists.Count + _deletedGroups.Count + _deletedLists.Count + TaskCount == 0;

        public void UpsertGroup(AccountTaskListGroup group)
        {
            if (!_deletedGroups.ContainsKey(group.Id))
                _groups[group.Id] = group;
        }

        public void UpsertList(AccountTaskList list)
        {
            if (!_deletedLists.ContainsKey(list.Id))
                _lists[list.Id] = list;
        }

        public void UpsertTask(AccountTask task)
        {
            if (!_deletedTasks.ContainsKey(task.Id))
                _tasks[task.Id] = task;
        }

        public void DeleteGroup(AccountTaskListGroup group)
        {
            _groups.Remove(group.Id);
            _deletedGroups[group.Id] = group;
        }

        public void DeleteList(AccountTaskList list)
        {
            _lists.Remove(list.Id);
            _deletedLists[list.Id] = list;
        }

        public void DeleteTask(AccountTask task)
        {
            _tasks.Remove(task.Id);
            _deletedTasks[task.Id] = task;
        }

        /// <summary>
        /// Rows with a pending local mutation are left out: the page shows that request's
        /// optimistic state until the request completes.
        /// </summary>
        public void UpsertChanged<T>(
            IEnumerable<T> current,
            IReadOnlyDictionary<Guid, string> signaturesBefore,
            Func<T, Guid> id,
            Func<T, string> signature,
            Func<T, TaskPendingMutation> pendingMutation,
            Action<T> upsert)
        {
            foreach (var entity in current)
            {
                if (pendingMutation(entity) != TaskPendingMutation.None)
                    continue;

                if (!signaturesBefore.TryGetValue(id(entity), out var previous) ||
                    !string.Equals(previous, signature(entity), StringComparison.Ordinal))
                {
                    upsert(entity);
                }
            }
        }
    }

    private static Dictionary<Guid, string> TaskSignatures(IEnumerable<AccountTask> tasks, IEnumerable<AccountTaskStep> steps, bool isReadOnly)
    {
        var stepsByTask = VisibleStepsByTask(steps);
        return tasks.ToDictionary(task => task.Id, task => TaskSignature(task, StepsOf(stepsByTask, task.Id), isReadOnly));
    }

    private static ServerTaskChanges CollectTaskChanges(
        AccountTaskList list,
        IReadOnlyDictionary<Guid, AccountTask> tasksBefore,
        IReadOnlyDictionary<Guid, string> signaturesBefore,
        IReadOnlyCollection<AccountTask> tasksAfter,
        IEnumerable<AccountTaskStep> stepsAfter)
    {
        var changes = new ServerTaskChanges(list.MailAccountId);
        var stepsByTask = VisibleStepsByTask(stepsAfter);
        var remaining = new HashSet<Guid>();

        foreach (var task in tasksAfter)
        {
            remaining.Add(task.Id);
            if (task.PendingMutation != TaskPendingMutation.None)
                continue;

            var steps = StepsOf(stepsByTask, task.Id);
            if (signaturesBefore.TryGetValue(task.Id, out var previous) &&
                string.Equals(previous, TaskSignature(task, steps, list.IsReadOnly), StringComparison.Ordinal))
            {
                continue;
            }

            changes.UpsertTask(SnapshotTask(task, steps, list.IsReadOnly));
        }

        foreach (var (id, task) in tasksBefore)
        {
            if (!remaining.Contains(id))
                changes.DeleteTask(task);
        }

        return changes;
    }

    private void ReportServerChanges(ServerTaskChanges changes)
    {
        if (changes.IsEmpty)
            return;

        if (changes.TaskCount > ServerTaskMessageLimit)
        {
            ReportUIChange(new BulkTaskChanged(changes.AccountId));
            return;
        }

        foreach (var group in changes.Groups)
            ReportUIChange(new TaskStateChanged(TaskSynchronizerOperation.UpdateGroup,
                null, null, null, OptimisticEntityChange.Upsert, EntityUpdateSource.Server, group));

        foreach (var list in changes.Lists)
            ReportUIChange(new TaskStateChanged(TaskSynchronizerOperation.UpdateList,
                list, null, null, OptimisticEntityChange.Upsert, EntityUpdateSource.Server));

        foreach (var task in changes.Tasks)
            ReportUIChange(new TaskStateChanged(TaskSynchronizerOperation.UpdateTask,
                null, task, null, OptimisticEntityChange.Upsert, EntityUpdateSource.Server));

        // Tasks go before their list, and lists before their group, so no surface briefly shows an orphan.
        foreach (var task in changes.DeletedTasks)
            ReportUIChange(new TaskStateChanged(TaskSynchronizerOperation.DeleteTask,
                null, task, null, OptimisticEntityChange.Delete, EntityUpdateSource.Server));

        foreach (var list in changes.DeletedLists)
            ReportUIChange(new TaskStateChanged(TaskSynchronizerOperation.DeleteList,
                list, null, null, OptimisticEntityChange.Delete, EntityUpdateSource.Server));

        foreach (var group in changes.DeletedGroups)
            ReportUIChange(new TaskStateChanged(TaskSynchronizerOperation.DeleteGroup,
                null, null, null, OptimisticEntityChange.Delete, EntityUpdateSource.Server, group));
    }

    private static Dictionary<Guid, List<AccountTaskStep>> VisibleStepsByTask(IEnumerable<AccountTaskStep> steps)
        => steps
            .Where(step => step.PendingMutation != TaskPendingMutation.Delete)
            .GroupBy(step => step.TaskId)
            .ToDictionary(group => group.Key, group => group.OrderBy(step => step.Order).ToList());

    private static List<AccountTaskStep> StepsOf(Dictionary<Guid, List<AccountTaskStep>> stepsByTask, Guid taskId)
        => stepsByTask.TryGetValue(taskId, out var steps) ? steps : [];

    /// <summary>
    /// The message carries copies: the stored rows can be the synchronizer's own delta objects,
    /// which the page must not share.
    /// </summary>
    private static AccountTask SnapshotTask(AccountTask task, List<AccountTaskStep> steps, bool isReadOnly)
        => new()
        {
            Id = task.Id,
            MailAccountId = task.MailAccountId,
            TaskListId = task.TaskListId,
            SourceKind = task.SourceKind,
            RemoteId = task.RemoteId,
            RemoteVersion = task.RemoteVersion,
            Title = task.Title,
            Notes = task.Notes,
            DueDate = task.DueDate,
            IsCompleted = task.IsCompleted,
            IsImportant = task.IsImportant,
            MyDayDateUtc = task.MyDayDateUtc,
            CompletedAtUtc = task.CompletedAtUtc,
            RemoteOrder = task.RemoteOrder,
            PendingMutation = task.PendingMutation,
            CreatedAtUtc = task.CreatedAtUtc,
            ModifiedAtUtc = task.ModifiedAtUtc,
            IsReadOnly = isReadOnly,
            Steps = steps.Select(step => new AccountTaskStep
            {
                Id = step.Id,
                TaskId = step.TaskId,
                MailAccountId = step.MailAccountId,
                SourceKind = step.SourceKind,
                RemoteId = step.RemoteId,
                RemoteVersion = step.RemoteVersion,
                Title = step.Title,
                IsCompleted = step.IsCompleted,
                Order = step.Order,
                PendingMutation = step.PendingMutation,
                CreatedAtUtc = step.CreatedAtUtc,
                ModifiedAtUtc = step.ModifiedAtUtc,
                IsReadOnly = isReadOnly
            }).ToList()
        };

    private static string GroupSignature(AccountTaskListGroup group)
        => Join(group.Title, group.SortOrder, group.IsExpanded);

    private static string ListSignature(AccountTaskList list)
        => Join(list.Title, list.ColorHex, list.GroupId, list.SortOrder, list.IsDefault, list.IsReadOnly, list.HoldsCalendarEvents,
            list.SortKind, list.SortAscending, list.ShowCompletedTasks);

    private static string TaskSignature(AccountTask task, IEnumerable<AccountTaskStep> steps, bool isReadOnly)
        => Join(task.Title, task.Notes, task.DueDate?.Ticks, task.IsCompleted, task.CompletedAtUtc?.Ticks,
            task.IsImportant, task.MyDayDateUtc?.Ticks, task.TaskListId, isReadOnly,
            string.Join('\u001e', steps.Select(step => Join(step.Id, step.Title, step.IsCompleted, step.Order))));

    private static string Join(params object[] values)
        => string.Join('\u001f', values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
}
