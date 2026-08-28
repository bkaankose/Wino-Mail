using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface ITaskService : ITaskQueryService
{
    Task<AccountTaskListGroup> CreateTaskListGroupAsync(Guid accountId, string title);
    Task UpdateTaskListGroupAsync(AccountTaskListGroup group);
    Task DeleteTaskListGroupAsync(Guid groupId, bool ungroupLists = true);
    Task UpdateTaskListPlacementAsync(Guid listId, Guid? groupId, int sortOrder);
    Task<AccountTaskSyncState> GetTaskSyncStateAsync(Guid accountId, TaskSourceKind sourceKind);
    Task ApplyTaskTopologyDeltaAsync(TaskTopologyDelta delta);
    Task ApplyTaskHierarchyDeltaAsync(TaskHierarchyDelta delta);

    /// <summary>
    /// Mirrors one provider-owned group, matched on its remote id. Local edits that the provider
    /// cannot round-trip (expansion state) are preserved.
    /// </summary>
    Task<AccountTaskListGroup> UpsertRemoteTaskListGroupAsync(AccountTaskListGroup group);

    /// <summary>Drops provider-owned groups for an account that the provider no longer reports.</summary>
    Task RemoveStaleRemoteTaskListGroupsAsync(Guid accountId, TaskSourceKind sourceKind, IReadOnlyCollection<string> activeRemoteIds);

    /// <summary>
    /// Applies the presentation state that only the substrate API exposes. Every argument is
    /// optional so an unrecognized value leaves the stored one alone.
    /// </summary>
    Task ApplyRemoteTaskListMetadataAsync(Guid listId, Guid? groupId, string colorHex, TaskSortKind? sortKind, bool? sortAscending, bool? showCompletedTasks);

    /// <summary>Stores the substrate delta cursors for every provider list on the account.</summary>
    Task CommitSubstrateDeltaLinksAsync(Guid accountId, TaskSourceKind sourceKind, string groupDeltaLink, string folderDeltaLink);
    Task<AccountTaskList> GetOrCreateLocalTaskListAsync(Guid accountId, string displayName);
    Task<AccountTaskList> CreateTaskListAsync(Guid accountId, string title);
    Task<AccountTaskList> UpsertRemoteTaskListAsync(AccountTaskList list);
    Task CommitTaskListDeltaLinkAsync(Guid listId, string listDeltaLink);
    Task UpdateTaskListAsync(AccountTaskList list);
    Task DeleteTaskListAsync(Guid listId);
    Task RemoveTaskListAsync(Guid listId);

    /// <summary>
    /// Open tasks worth pulling into today's My Day, ordered overdue → due today →
    /// unfinished from yesterday's My Day → recently created. Anything already in
    /// today's My Day is excluded.
    /// </summary>
    Task<AccountTask> CreateTaskAsync(AccountTask task);
    Task UpdateTaskAsync(AccountTask task);
    Task DeleteTaskAsync(Guid taskId);
    Task<AccountTaskStep> CreateStepAsync(AccountTaskStep step);
    Task UpdateStepAsync(AccountTaskStep step);
    Task DeleteStepAsync(Guid stepId);

    Task CompleteListMutationAsync(Guid listId, AccountTaskList remoteList, bool deleted);
    Task CompleteTaskListGroupMutationAsync(Guid groupId, AccountTaskListGroup remoteGroup, bool deleted);
    Task CompleteTaskListPlacementMutationAsync(Guid listId, AccountTaskList remoteList);
    Task CompleteTaskMutationAsync(Guid taskId, AccountTask remoteTask, bool deleted);
    Task CompleteStepMutationAsync(Guid stepId, AccountTaskStep remoteStep, bool deleted);
    Task DeleteAccountTasksAsync(Guid accountId);
    Task DeleteTaskListsBySourceAsync(Guid accountId, TaskSourceKind sourceKind);
    Task MarkTaskListsReadOnlyAsync(Guid accountId, TaskSourceKind sourceKind, bool isReadOnly = true);
    Task EnsureLocalTaskListAsync(Guid accountId, string displayName);
}
