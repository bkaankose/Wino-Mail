using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface ITaskService
{
    Task<List<AccountTaskList>> GetTaskListsAsync(Guid? accountId = null);
    Task<AccountTaskList> GetTaskListAsync(Guid listId);
    Task<AccountTaskList> GetOrCreateLocalTaskListAsync(Guid accountId, string displayName);
    Task<AccountTaskList> CreateTaskListAsync(Guid accountId, string title);
    Task<AccountTaskList> UpsertRemoteTaskListAsync(AccountTaskList list);
    Task CommitTaskListDeltaLinkAsync(Guid listId, string listDeltaLink);
    Task UpdateTaskListAsync(AccountTaskList list);
    Task DeleteTaskListAsync(Guid listId);
    Task RemoveTaskListAsync(Guid listId);

    Task<List<AccountTask>> GetTasksAsync(Guid? accountId = null, Guid? listId = null, TaskViewKind view = TaskViewKind.All, string search = null, TaskSortKind sort = TaskSortKind.DueDate);

    /// <summary>
    /// Open tasks worth pulling into today's My Day, ordered overdue → due today →
    /// unfinished from yesterday's My Day → recently created. Anything already in
    /// today's My Day is excluded.
    /// </summary>
    Task<List<AccountTask>> GetMyDaySuggestionsAsync();
    Task<AccountTask> GetTaskAsync(Guid taskId);
    Task<AccountTask> CreateTaskAsync(AccountTask task);
    Task UpdateTaskAsync(AccountTask task);
    Task DeleteTaskAsync(Guid taskId);
    Task<AccountTaskStep> CreateStepAsync(AccountTaskStep step);
    Task UpdateStepAsync(AccountTaskStep step);
    Task DeleteStepAsync(Guid stepId);

    Task CompleteListMutationAsync(Guid listId, AccountTaskList remoteList, bool deleted);
    Task CompleteTaskMutationAsync(Guid taskId, AccountTask remoteTask, bool deleted);
    Task CompleteStepMutationAsync(Guid stepId, AccountTaskStep remoteStep, bool deleted);
    Task ReplaceListAsync(Guid listId, IReadOnlyList<AccountTask> tasks, string taskDeltaLink, DateTime? watermarkUtc = null, string listDeltaLink = null);
    Task DeleteAccountTasksAsync(Guid accountId);
    Task DeleteTaskListsBySourceAsync(Guid accountId, TaskSourceKind sourceKind);
    Task MarkTaskListsReadOnlyAsync(Guid accountId, TaskSourceKind sourceKind, bool isReadOnly = true);
    Task EnsureLocalTaskListAsync(Guid accountId, string displayName);
}
