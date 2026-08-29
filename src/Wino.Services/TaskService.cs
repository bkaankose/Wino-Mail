using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Domain.Misc;

namespace Wino.Services;

/// <summary>
/// Owns the account task cache and optimistic local mutations. Provider synchronizers
/// reconcile this cache; this service never performs network I/O.
/// </summary>
public sealed class TaskService : BaseDatabaseService, ITaskService
{
    private static readonly SemaphoreSlim TaskListColorGate = new(1, 1);

    public TaskService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<List<AccountTaskListGroup>> GetTaskListGroupsAsync(Guid? accountId = null)
    {
        var groups = await Connection.Table<AccountTaskListGroup>().ToListAsync().ConfigureAwait(false);
        return (accountId is null ? groups : groups.Where(group => group.MailAccountId == accountId.Value))
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Id)
            .ToList();
    }

    public async Task<AccountTaskListGroup> CreateTaskListGroupAsync(Guid accountId, string title)
    {
        var existing = await GetTaskListGroupsAsync(accountId).ConfigureAwait(false);
        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(item => item.Id == accountId).ConfigureAwait(false);
        var sourceKind = account is null ? TaskSourceKind.Local : ResolveSource(account);
        var now = DateTime.UtcNow;
        var group = new AccountTaskListGroup
        {
            MailAccountId = accountId,
            SourceKind = sourceKind == TaskSourceKind.Outlook ? TaskSourceKind.Outlook : TaskSourceKind.Local,
            Title = string.IsNullOrWhiteSpace(title) ? "Group" : title.Trim(),
            SortOrder = existing.Count,
            RemoteOrder = DateTimeOffset.UtcNow.AddSeconds(existing.Count).ToString("O"),
            PendingMutation = sourceKind == TaskSourceKind.Outlook ? TaskPendingMutation.Create : TaskPendingMutation.None,
            CreatedAtUtc = now,
            ModifiedAtUtc = now
        };
        await Connection.InsertAsync(group, typeof(AccountTaskListGroup)).ConfigureAwait(false);
        return group;
    }

    public async Task UpdateTaskListGroupAsync(AccountTaskListGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.Title = string.IsNullOrWhiteSpace(group.Title) ? "Group" : group.Title.Trim();
        group.ModifiedAtUtc = DateTime.UtcNow;
        if (group.SourceKind == TaskSourceKind.Outlook && group.PendingMutation == TaskPendingMutation.None)
            group.PendingMutation = TaskPendingMutation.Update;
        await Connection.UpdateAsync(group, typeof(AccountTaskListGroup)).ConfigureAwait(false);
    }

    public async Task DeleteTaskListGroupAsync(Guid groupId, bool ungroupLists = true)
    {
        if (ungroupLists)
            await Connection.ExecuteAsync("UPDATE TaskList SET GroupId = NULL WHERE GroupId = ?", groupId).ConfigureAwait(false);
        await Connection.DeleteAsync<AccountTaskListGroup>(groupId).ConfigureAwait(false);
    }

    public async Task CompleteTaskListGroupMutationAsync(Guid groupId, AccountTaskListGroup remoteGroup, bool deleted)
    {
        var stored = await Connection.Table<AccountTaskListGroup>()
            .FirstOrDefaultAsync(group => group.Id == groupId).ConfigureAwait(false);
        if (deleted)
        {
            if (stored is not null)
                await DeleteTaskListGroupAsync(groupId, ungroupLists: false).ConfigureAwait(false);
            return;
        }

        if (remoteGroup is null)
            throw new InvalidOperationException("A successful task-group mutation requires a group snapshot.");

        if (stored is null)
        {
            remoteGroup.Id = groupId;
            remoteGroup.PendingMutation = TaskPendingMutation.None;
            remoteGroup.CreatedAtUtc = remoteGroup.CreatedAtUtc == default ? DateTime.UtcNow : remoteGroup.CreatedAtUtc;
            remoteGroup.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.InsertAsync(remoteGroup, typeof(AccountTaskListGroup)).ConfigureAwait(false);
            return;
        }

        stored.RemoteId = remoteGroup.RemoteId ?? stored.RemoteId;
        stored.RemoteVersion = remoteGroup.RemoteVersion ?? stored.RemoteVersion;
        stored.RemoteOrder = remoteGroup.RemoteOrder ?? stored.RemoteOrder;
        stored.Title = remoteGroup.Title ?? stored.Title;
        stored.SortOrder = remoteGroup.SortOrder;
        stored.PendingMutation = TaskPendingMutation.None;
        stored.ModifiedAtUtc = DateTime.UtcNow;
        await Connection.UpdateAsync(stored, typeof(AccountTaskListGroup)).ConfigureAwait(false);
    }

    public async Task CompleteTaskListPlacementMutationAsync(Guid listId, AccountTaskList remoteList)
    {
        var stored = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (stored is null || remoteList is null)
            return;
        stored.GroupId = remoteList.GroupId;
        stored.SortOrder = remoteList.SortOrder;
        stored.RemoteOrder = remoteList.RemoteOrder ?? stored.RemoteOrder;
        stored.RemoteVersion = remoteList.RemoteVersion ?? stored.RemoteVersion;
        stored.PendingMutation = TaskPendingMutation.None;
        stored.ModifiedAtUtc = DateTime.UtcNow;
        await Connection.UpdateAsync(stored, typeof(AccountTaskList)).ConfigureAwait(false);
    }

    public async Task<AccountTaskListGroup> UpsertRemoteTaskListGroupAsync(AccountTaskListGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var existing = await Connection.Table<AccountTaskListGroup>()
            .FirstOrDefaultAsync(item => item.MailAccountId == group.MailAccountId &&
                                         item.SourceKind == group.SourceKind &&
                                         item.RemoteId == group.RemoteId).ConfigureAwait(false);

        group.Title = string.IsNullOrWhiteSpace(group.Title) ? "Group" : group.Title.Trim();
        group.ModifiedAtUtc = DateTime.UtcNow;

        if (existing is not null)
        {
            group.Id = existing.Id;
            group.CreatedAtUtc = existing.CreatedAtUtc;
            // Expansion is a local preference; the provider has no notion of it.
            group.IsExpanded = existing.IsExpanded;
            await Connection.UpdateAsync(group, typeof(AccountTaskListGroup)).ConfigureAwait(false);
            return group;
        }

        group.Id = group.Id == Guid.Empty ? Guid.NewGuid() : group.Id;
        group.CreatedAtUtc = DateTime.UtcNow;
        await Connection.InsertAsync(group, typeof(AccountTaskListGroup)).ConfigureAwait(false);
        return group;
    }

    public async Task RemoveStaleRemoteTaskListGroupsAsync(
        Guid accountId, TaskSourceKind sourceKind, IReadOnlyCollection<string> activeRemoteIds)
    {
        var stored = await Connection.Table<AccountTaskListGroup>()
            .Where(item => item.MailAccountId == accountId && item.SourceKind == sourceKind)
            .ToListAsync().ConfigureAwait(false);

        foreach (var group in stored)
        {
            if (group.RemoteId is not null && activeRemoteIds.Contains(group.RemoteId))
                continue;

            // Lists fall back to ungrouped rather than disappearing with the group.
            await DeleteTaskListGroupAsync(group.Id).ConfigureAwait(false);
        }
    }

    public async Task ApplyRemoteTaskListMetadataAsync(
        Guid listId, Guid? groupId, string colorHex, TaskSortKind? sortKind, bool? sortAscending, bool? showCompletedTasks)
    {
        var list = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (list is null)
            return;

        list.GroupId = groupId;

        if (!string.IsNullOrWhiteSpace(colorHex))
            list.ColorHex = colorHex;
        if (sortKind.HasValue)
            list.SortKind = sortKind;
        if (sortAscending.HasValue)
            list.SortAscending = sortAscending.Value;
        if (showCompletedTasks.HasValue)
            list.ShowCompletedTasks = showCompletedTasks.Value;

        list.ModifiedAtUtc = DateTime.UtcNow;
        await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
    }

    public async Task CommitSubstrateDeltaLinksAsync(
        Guid accountId, TaskSourceKind sourceKind, string groupDeltaLink, string folderDeltaLink)
    {
        if (string.IsNullOrWhiteSpace(groupDeltaLink) && string.IsNullOrWhiteSpace(folderDeltaLink))
            return;

        var lists = await Connection.Table<AccountTaskList>()
            .Where(item => item.MailAccountId == accountId && item.SourceKind == sourceKind)
            .ToListAsync().ConfigureAwait(false);

        foreach (var list in lists)
        {
            if (!string.IsNullOrWhiteSpace(groupDeltaLink))
                list.SubstrateGroupDeltaLink = groupDeltaLink;
            if (!string.IsNullOrWhiteSpace(folderDeltaLink))
                list.SubstrateFolderDeltaLink = folderDeltaLink;

            await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
        }
    }

    public async Task UpdateTaskListPlacementAsync(Guid listId, Guid? groupId, int sortOrder)
    {
        var list = await GetTaskListAsync(listId).ConfigureAwait(false);
        if (list is null)
            return;
        list.GroupId = groupId;
        list.SortOrder = sortOrder;
        list.ModifiedAtUtc = DateTime.UtcNow;
        await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
    }

    public async Task<AccountTaskSyncState> GetTaskSyncStateAsync(Guid accountId, TaskSourceKind sourceKind)
    {
        var state = await Connection.Table<AccountTaskSyncState>()
            .FirstOrDefaultAsync(item => item.MailAccountId == accountId && item.SourceKind == sourceKind)
            .ConfigureAwait(false);
        if (state is not null)
            return state;

        var legacyLists = await Connection.Table<AccountTaskList>()
            .Where(list => list.MailAccountId == accountId && list.SourceKind == sourceKind)
            .ToListAsync().ConfigureAwait(false);
        state = new AccountTaskSyncState
        {
            MailAccountId = accountId,
            SourceKind = sourceKind,
            ListDeltaLink = legacyLists.Select(list => list.ListDeltaLink).FirstOrDefault(link => !string.IsNullOrWhiteSpace(link)),
            SubstrateGroupDeltaLink = legacyLists.Select(list => list.SubstrateGroupDeltaLink).FirstOrDefault(link => !string.IsNullOrWhiteSpace(link)),
            SubstrateFolderDeltaLink = legacyLists.Select(list => list.SubstrateFolderDeltaLink).FirstOrDefault(link => !string.IsNullOrWhiteSpace(link)),
            LastSuccessfulSyncUtc = legacyLists.Count == 0 ? null : legacyLists.Max(list => list.LastSuccessfulSyncUtc)
        };
        await Connection.InsertAsync(state, typeof(AccountTaskSyncState)).ConfigureAwait(false);
        return state;
    }

    public async Task ApplyTaskTopologyDeltaAsync(TaskTopologyDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var groups = await Connection.Table<AccountTaskListGroup>()
            .Where(group => group.MailAccountId == delta.MailAccountId && group.SourceKind == delta.SourceKind)
            .ToListAsync().ConfigureAwait(false);
        var lists = await Connection.Table<AccountTaskList>()
            .Where(list => list.MailAccountId == delta.MailAccountId && list.SourceKind == delta.SourceKind)
            .ToListAsync().ConfigureAwait(false);
        var state = await GetTaskSyncStateAsync(delta.MailAccountId, delta.SourceKind).ConfigureAwait(false);
        var deletedGroupIds = new HashSet<string>(delta.DeletedGroupRemoteIds ?? [], StringComparer.Ordinal);
        var deletedListIds = new HashSet<string>(delta.DeletedListRemoteIds ?? [], StringComparer.Ordinal);
        var activeGroupIds = new HashSet<string>((delta.Groups ?? []).Select(group => group.RemoteId).Where(id => id is not null), StringComparer.Ordinal);
        var activeListIds = new HashSet<string>((delta.Lists ?? []).Select(list => list.RemoteId).Where(id => id is not null), StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var incoming in delta.Groups ?? [])
            {
                if (string.IsNullOrWhiteSpace(incoming.RemoteId))
                    continue;

                var stored = groups.FirstOrDefault(group => string.Equals(group.RemoteId, incoming.RemoteId, StringComparison.Ordinal))
                    ?? groups.FirstOrDefault(group => group.PendingMutation == TaskPendingMutation.Create &&
                                                     !string.IsNullOrWhiteSpace(group.RemoteOrder) &&
                                                     string.Equals(group.RemoteOrder, incoming.RemoteOrder, StringComparison.Ordinal));
                if (stored is null)
                {
                    incoming.Id = incoming.Id == Guid.Empty ? Guid.NewGuid() : incoming.Id;
                    incoming.MailAccountId = delta.MailAccountId;
                    incoming.SourceKind = delta.SourceKind;
                    incoming.PendingMutation = TaskPendingMutation.None;
                    incoming.CreatedAtUtc = now;
                    incoming.ModifiedAtUtc = now;
                    transaction.Insert(incoming, typeof(AccountTaskListGroup));
                    groups.Add(incoming);
                    continue;
                }

                var completesPendingCreate = stored.PendingMutation == TaskPendingMutation.Create;
                stored.RemoteId = incoming.RemoteId;
                stored.RemoteVersion = incoming.RemoteVersion ?? stored.RemoteVersion;
                stored.RemoteOrder = incoming.RemoteOrder ?? stored.RemoteOrder;
                if (stored.PendingMutation == TaskPendingMutation.None)
                    stored.Title = incoming.Title ?? stored.Title;
                if (completesPendingCreate)
                    stored.PendingMutation = TaskPendingMutation.None;
                stored.ModifiedAtUtc = now;
                transaction.Update(stored, typeof(AccountTaskListGroup));
            }

            foreach (var stored in groups.ToList())
            {
                var remove = !string.IsNullOrWhiteSpace(stored.RemoteId) &&
                             (deletedGroupIds.Contains(stored.RemoteId) ||
                              delta.ReconcileGroups && !activeGroupIds.Contains(stored.RemoteId));
                if (!remove || stored.PendingMutation != TaskPendingMutation.None)
                    continue;

                transaction.Execute("UPDATE TaskList SET GroupId = NULL WHERE GroupId = ?", stored.Id);
                transaction.Execute("DELETE FROM TaskListGroup WHERE Id = ?", stored.Id);
                groups.Remove(stored);
                foreach (var list in lists.Where(list => list.GroupId == stored.Id))
                    list.GroupId = null;
            }

            foreach (var incoming in delta.Lists ?? [])
            {
                if (string.IsNullOrWhiteSpace(incoming.RemoteId))
                    continue;

                var stored = lists.FirstOrDefault(list => string.Equals(list.RemoteId, incoming.RemoteId, StringComparison.Ordinal));
                if (stored is null)
                {
                    incoming.Id = incoming.Id == Guid.Empty ? Guid.NewGuid() : incoming.Id;
                    incoming.MailAccountId = delta.MailAccountId;
                    incoming.SourceKind = delta.SourceKind;
                    incoming.PendingMutation = TaskPendingMutation.None;
                    incoming.CreatedAtUtc = now;
                    incoming.ModifiedAtUtc = now;
                    transaction.Insert(incoming, typeof(AccountTaskList));
                    lists.Add(incoming);
                    continue;
                }

                stored.RemoteVersion = incoming.RemoteVersion ?? stored.RemoteVersion;
                if (stored.PendingMutation == TaskPendingMutation.None)
                {
                    stored.Title = incoming.Title ?? stored.Title;
                    stored.IsDefault = incoming.IsDefault;
                    stored.IsReadOnly = incoming.IsReadOnly;
                }
                stored.ModifiedAtUtc = now;
                transaction.Update(stored, typeof(AccountTaskList));
            }

            foreach (var stored in lists.ToList())
            {
                var remove = !string.IsNullOrWhiteSpace(stored.RemoteId) &&
                             (deletedListIds.Contains(stored.RemoteId) ||
                              delta.ReconcileLists && !activeListIds.Contains(stored.RemoteId));
                if (!remove || stored.PendingMutation != TaskPendingMutation.None)
                    continue;

                DeleteList(transaction, stored.Id);
                lists.Remove(stored);
            }

            var groupsByRemoteId = groups.Where(group => !string.IsNullOrWhiteSpace(group.RemoteId))
                .ToDictionary(group => group.RemoteId, StringComparer.Ordinal);
            foreach (var placement in delta.Placements ?? [])
            {
                var list = lists.FirstOrDefault(item => string.Equals(item.RemoteId, placement.ListRemoteId, StringComparison.Ordinal));
                if (list is null || list.PendingMutation != TaskPendingMutation.None)
                    continue;

                list.GroupId = !string.IsNullOrWhiteSpace(placement.GroupRemoteId) &&
                               groupsByRemoteId.TryGetValue(placement.GroupRemoteId, out var group)
                    ? group.Id
                    : null;
                list.RemoteVersion = placement.RemoteVersion ?? list.RemoteVersion;
                list.RemoteOrder = placement.RemoteOrder ?? list.RemoteOrder;
                if (!string.IsNullOrWhiteSpace(placement.ColorHex))
                    list.ColorHex = placement.ColorHex;
                if (placement.SortKind.HasValue)
                    list.SortKind = placement.SortKind;
                if (placement.SortAscending.HasValue)
                    list.SortAscending = placement.SortAscending.Value;
                if (placement.ShowCompletedTasks.HasValue)
                    list.ShowCompletedTasks = placement.ShowCompletedTasks.Value;
                list.ModifiedAtUtc = now;
                transaction.Update(list, typeof(AccountTaskList));
            }

            var orderedGroups = groups.OrderBy(group => group.RemoteOrder ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Id).ToList();
            for (var index = 0; index < orderedGroups.Count; index++)
            {
                if (orderedGroups[index].SortOrder == index)
                    continue;
                orderedGroups[index].SortOrder = index;
                transaction.Update(orderedGroups[index], typeof(AccountTaskListGroup));
            }

            foreach (var siblingSet in lists.GroupBy(list => list.GroupId))
            {
                var orderedLists = siblingSet.OrderBy(list => list.RemoteOrder ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(list => list.Title, StringComparer.OrdinalIgnoreCase).ThenBy(list => list.Id).ToList();
                for (var index = 0; index < orderedLists.Count; index++)
                {
                    if (orderedLists[index].SortOrder == index)
                        continue;
                    orderedLists[index].SortOrder = index;
                    transaction.Update(orderedLists[index], typeof(AccountTaskList));
                }
            }

            if (!string.IsNullOrWhiteSpace(delta.ListDeltaLink))
                state.ListDeltaLink = delta.ListDeltaLink;
            if (!string.IsNullOrWhiteSpace(delta.SubstrateGroupDeltaLink))
                state.SubstrateGroupDeltaLink = delta.SubstrateGroupDeltaLink;
            if (!string.IsNullOrWhiteSpace(delta.SubstrateFolderDeltaLink))
                state.SubstrateFolderDeltaLink = delta.SubstrateFolderDeltaLink;
            state.LastSuccessfulSyncUtc = now;
            transaction.Update(state, typeof(AccountTaskSyncState));
        }).ConfigureAwait(false);
    }

    public async Task ApplyTaskHierarchyDeltaAsync(TaskHierarchyDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var list = await GetTaskListAsync(delta.TaskListId).ConfigureAwait(false);
        if (list is null)
            return;

        var tasks = await Connection.Table<AccountTask>().Where(task => task.TaskListId == list.Id).ToListAsync().ConfigureAwait(false);
        var taskIds = tasks.Select(task => task.Id).ToList();
        var steps = taskIds.Count == 0
            ? []
            : (await Connection.Table<AccountTaskStep>().ToListAsync().ConfigureAwait(false)).Where(step => taskIds.Contains(step.TaskId)).ToList();
        var deletedTasks = new HashSet<string>(delta.DeletedTaskRemoteIds ?? [], StringComparer.Ordinal);
        var deletedSteps = new HashSet<string>(delta.DeletedStepRemoteIds ?? [], StringComparer.Ordinal);
        var authoritativeParents = new HashSet<string>(delta.AuthoritativeStepParentRemoteIds ?? [], StringComparer.Ordinal);
        var incomingRemoteIds = new HashSet<string>((delta.Tasks ?? []).Select(task => task.RemoteId).Where(id => id is not null), StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var remoteId in deletedTasks)
            {
                var stored = tasks.FirstOrDefault(task => string.Equals(task.RemoteId, remoteId, StringComparison.Ordinal));
                if (stored is null || stored.PendingMutation != TaskPendingMutation.None)
                    continue;
                transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", stored.Id);
                transaction.Execute("DELETE FROM TaskCard WHERE Id = ?", stored.Id);
                steps.RemoveAll(step => step.TaskId == stored.Id);
                tasks.Remove(stored);
            }

            foreach (var remoteId in deletedSteps)
            {
                foreach (var stored in steps.Where(step => step.PendingMutation == TaskPendingMutation.None &&
                                                           string.Equals(step.RemoteId, remoteId, StringComparison.Ordinal)).ToList())
                {
                    transaction.Execute("DELETE FROM TaskStep WHERE Id = ?", stored.Id);
                    steps.Remove(stored);
                }
            }

            foreach (var incoming in delta.Tasks ?? [])
            {
                if (string.IsNullOrWhiteSpace(incoming.RemoteId))
                    continue;

                // A Google item can move between the root collection and a parent's child collection.
                var promotedSteps = steps.Where(step => string.Equals(step.RemoteId, incoming.RemoteId, StringComparison.Ordinal)).ToList();
                if (promotedSteps.Any(step => step.PendingMutation != TaskPendingMutation.None))
                    continue;
                if (promotedSteps.Count > 0)
                    incoming.Id = promotedSteps[0].Id;
                foreach (var promotedStep in promotedSteps)
                {
                    transaction.Execute("DELETE FROM TaskStep WHERE Id = ?", promotedStep.Id);
                    steps.Remove(promotedStep);
                }

                var stored = tasks.FirstOrDefault(task => string.Equals(task.RemoteId, incoming.RemoteId, StringComparison.Ordinal))
                    ?? tasks.FirstOrDefault(task => task.Id == incoming.Id);
                if (stored is null)
                {
                    incoming.Id = incoming.Id == Guid.Empty ? Guid.NewGuid() : incoming.Id;
                    incoming.MailAccountId = list.MailAccountId;
                    incoming.TaskListId = list.Id;
                    incoming.SourceKind = list.SourceKind;
                    incoming.PendingMutation = TaskPendingMutation.None;
                    incoming.CreatedAtUtc = now;
                    incoming.ModifiedAtUtc = now;
                    transaction.Insert(incoming, typeof(AccountTask));
                    stored = incoming;
                    tasks.Add(stored);
                }
                else
                {
                    stored.RemoteId = incoming.RemoteId;
                    stored.RemoteVersion = incoming.RemoteVersion ?? stored.RemoteVersion;
                    stored.RemoteOrder = incoming.RemoteOrder ?? stored.RemoteOrder;
                    if (stored.PendingMutation == TaskPendingMutation.None)
                    {
                        stored.Title = incoming.Title ?? stored.Title;
                        stored.Notes = incoming.Notes;
                        stored.DueDate = incoming.DueDate;
                        stored.IsCompleted = incoming.IsCompleted;
                        stored.CompletedAtUtc = incoming.CompletedAtUtc;
                        if (stored.SourceKind == TaskSourceKind.Outlook)
                            stored.IsImportant = incoming.IsImportant;
                    }
                    stored.ModifiedAtUtc = now;
                    transaction.Update(stored, typeof(AccountTask));
                }

                var incomingStepRemoteIds = new HashSet<string>((incoming.Steps ?? []).Select(step => step.RemoteId).Where(id => id is not null), StringComparer.Ordinal);
                foreach (var incomingStep in incoming.Steps ?? [])
                {
                    if (string.IsNullOrWhiteSpace(incomingStep.RemoteId))
                        continue;

                    var demotedTasks = tasks.Where(task => task.Id != stored.Id && string.Equals(task.RemoteId, incomingStep.RemoteId, StringComparison.Ordinal)).ToList();
                    if (demotedTasks.Any(task => task.PendingMutation != TaskPendingMutation.None))
                        continue;
                    if (demotedTasks.Count > 0)
                        incomingStep.Id = demotedTasks[0].Id;
                    foreach (var demotedTask in demotedTasks)
                    {
                        transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", demotedTask.Id);
                        transaction.Execute("DELETE FROM TaskCard WHERE Id = ?", demotedTask.Id);
                        steps.RemoveAll(step => step.TaskId == demotedTask.Id);
                        tasks.Remove(demotedTask);
                    }

                    var storedStep = steps.FirstOrDefault(step => string.Equals(step.RemoteId, incomingStep.RemoteId, StringComparison.Ordinal))
                        ?? steps.FirstOrDefault(step => step.Id == incomingStep.Id);
                    if (storedStep is null)
                    {
                        incomingStep.Id = incomingStep.Id == Guid.Empty ? Guid.NewGuid() : incomingStep.Id;
                        incomingStep.TaskId = stored.Id;
                        incomingStep.MailAccountId = list.MailAccountId;
                        incomingStep.SourceKind = list.SourceKind;
                        incomingStep.PendingMutation = TaskPendingMutation.None;
                        incomingStep.CreatedAtUtc = now;
                        incomingStep.ModifiedAtUtc = now;
                        transaction.Insert(incomingStep, typeof(AccountTaskStep));
                        steps.Add(incomingStep);
                    }
                    else
                    {
                        storedStep.TaskId = stored.Id;
                        storedStep.RemoteId = incomingStep.RemoteId;
                        storedStep.RemoteVersion = incomingStep.RemoteVersion ?? storedStep.RemoteVersion;
                        if (storedStep.PendingMutation == TaskPendingMutation.None)
                        {
                            storedStep.Title = incomingStep.Title ?? storedStep.Title;
                            storedStep.IsCompleted = incomingStep.IsCompleted;
                            storedStep.Order = incomingStep.Order;
                        }
                        storedStep.ModifiedAtUtc = now;
                        transaction.Update(storedStep, typeof(AccountTaskStep));
                    }
                }

                if (authoritativeParents.Contains(incoming.RemoteId))
                {
                    foreach (var staleStep in steps.Where(step => step.TaskId == stored.Id &&
                                                                  step.PendingMutation == TaskPendingMutation.None &&
                                                                  !string.IsNullOrWhiteSpace(step.RemoteId) &&
                                                                  !incomingStepRemoteIds.Contains(step.RemoteId)).ToList())
                    {
                        transaction.Execute("DELETE FROM TaskStep WHERE Id = ?", staleStep.Id);
                        steps.Remove(staleStep);
                    }
                }
            }

            if (delta.IsFullSnapshot)
            {
                foreach (var stale in tasks.Where(task => task.PendingMutation == TaskPendingMutation.None &&
                                                          !string.IsNullOrWhiteSpace(task.RemoteId) &&
                                                          !incomingRemoteIds.Contains(task.RemoteId)).ToList())
                {
                    transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", stale.Id);
                    transaction.Execute("DELETE FROM TaskCard WHERE Id = ?", stale.Id);
                }
            }

            // Graph describes one task under two id encodings — the create response returns one,
            // the delta another — so an earlier sync could store the same item twice. A change key
            // belongs to a single item version, so rows sharing one are copies of each other. The
            // copy the delta just reported is kept, since that is the id future syncs will use.
            foreach (var duplicates in tasks
                         .Where(task => task.PendingMutation == TaskPendingMutation.None &&
                                        !string.IsNullOrWhiteSpace(task.RemoteVersion))
                         .GroupBy(task => task.RemoteVersion, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                var survivor = duplicates.FirstOrDefault(task => incomingRemoteIds.Contains(task.RemoteId)) ?? duplicates.First();

                foreach (var stale in duplicates.Where(task => task.Id != survivor.Id))
                {
                    transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", stale.Id);
                    transaction.Execute("DELETE FROM TaskCard WHERE Id = ?", stale.Id);
                }
            }

            if (delta.TaskDeltaLink is not null)
                list.TaskDeltaLink = delta.TaskDeltaLink;
            if (delta.WatermarkUtc.HasValue)
                list.WatermarkUtc = delta.WatermarkUtc;
            list.LastSuccessfulSyncUtc = now;
            transaction.Update(list, typeof(AccountTaskList));
        }).ConfigureAwait(false);
    }

    private static void DeleteList(SQLite.SQLiteConnection transaction, Guid listId)
    {
        transaction.Execute("DELETE FROM TaskStep WHERE TaskId IN (SELECT Id FROM TaskCard WHERE TaskListId = ?)", listId);
        transaction.Execute("DELETE FROM TaskCard WHERE TaskListId = ?", listId);
        transaction.Execute("DELETE FROM TaskList WHERE Id = ?", listId);
    }

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
        await InsertTaskListWithDistinctColorAsync(createdList).ConfigureAwait(false);
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
        await InsertTaskListWithDistinctColorAsync(list).ConfigureAwait(false);
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
            list.ColorHex = existing.ColorHex;
            list.GroupId = existing.GroupId;
            list.SortOrder = existing.SortOrder;
            list.PendingMutation = TaskPendingMutation.None;
            list.ModifiedAtUtc = DateTime.UtcNow;
            await Connection.UpdateAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
            return list;
        }

        list.Id = list.Id == Guid.Empty ? Guid.NewGuid() : list.Id;
        list.PendingMutation = TaskPendingMutation.None;
        list.CreatedAtUtc = DateTime.UtcNow;
        list.ModifiedAtUtc = list.CreatedAtUtc;
        await InsertTaskListWithDistinctColorAsync(list).ConfigureAwait(false);
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
            TaskViewKind.Planned => filtered.Where(task => task.DueDate.HasValue),
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

        if (deleted)
        {
            if (local is not null)
                await DeleteTaskListLocallyAsync(local).ConfigureAwait(false);
            return;
        }

        if (local is null)
        {
            if (remoteList is null)
                throw new InvalidOperationException("A successful task-list mutation requires a list snapshot.");

            remoteList.Id = listId;
            remoteList.PendingMutation = TaskPendingMutation.None;
            remoteList.CreatedAtUtc = remoteList.CreatedAtUtc == default ? DateTime.UtcNow : remoteList.CreatedAtUtc;
            remoteList.ModifiedAtUtc = DateTime.UtcNow;

            await InsertTaskListWithDistinctColorAsync(remoteList).ConfigureAwait(false);
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

    private async Task InsertTaskListWithDistinctColorAsync(AccountTaskList list)
    {
        await TaskListColorGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existingLists = await Connection.Table<AccountTaskList>().ToListAsync().ConfigureAwait(false);
            var usedColors = existingLists
                .Where(existing => existing.Id != list.Id && !string.IsNullOrWhiteSpace(existing.ColorHex))
                .Select(existing => existing.ColorHex)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(list.ColorHex) || usedColors.Contains(list.ColorHex))
                list.ColorHex = ColorPalette.GetDistinctColor(existingLists
                    .Where(existing => existing.Id != list.Id)
                    .Select(existing => existing.ColorHex));

            await Connection.InsertAsync(list, typeof(AccountTaskList)).ConfigureAwait(false);
        }
        finally
        {
            TaskListColorGate.Release();
        }
    }

    public async Task CompleteTaskMutationAsync(Guid taskId, AccountTask remoteTask, bool deleted)
    {
        var local = await GetTaskAsync(taskId).ConfigureAwait(false);

        if (deleted)
        {
            if (local is not null)
            {
                await Connection.Table<AccountTaskStep>().DeleteAsync(step => step.TaskId == taskId).ConfigureAwait(false);
                await Connection.ExecuteAsync("DELETE FROM TaskCard WHERE Id = ?", local.Id).ConfigureAwait(false);
            }
            return;
        }

        if (local is null)
        {
            if (remoteTask is null)
                throw new InvalidOperationException("A successful task mutation requires a task snapshot.");

            // A provider reconciliation can store the newly created task before this commit runs,
            // under an identity of its own. Adopting that row keeps one task per remote id instead
            // of leaving the create's own copy beside it, which is what made a new task show twice.
            local = await FindTaskByRemoteIdAsync(remoteTask).ConfigureAwait(false);
            if (local is not null)
            {
                await CompleteTaskMutationOnExistingAsync(local, remoteTask).ConfigureAwait(false);
                return;
            }

            remoteTask.Id = taskId;
            remoteTask.PendingMutation = TaskPendingMutation.None;
            remoteTask.CreatedAtUtc = remoteTask.CreatedAtUtc == default ? DateTime.UtcNow : remoteTask.CreatedAtUtc;
            remoteTask.ModifiedAtUtc = DateTime.UtcNow;

            await Connection.RunInTransactionAsync(transaction =>
            {
                transaction.Insert(remoteTask, typeof(AccountTask));

                foreach (var step in remoteTask.Steps ?? [])
                {
                    step.TaskId = taskId;
                    step.MailAccountId = remoteTask.MailAccountId;
                    step.SourceKind = remoteTask.SourceKind;
                    step.PendingMutation = TaskPendingMutation.None;
                    transaction.Insert(step, typeof(AccountTaskStep));
                }
            }).ConfigureAwait(false);
            return;
        }

        await CompleteTaskMutationOnExistingAsync(local, remoteTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds a stored task that already represents the same remote item, so a create commit can
    /// adopt it instead of adding a second row for one remote task.
    /// </summary>
    private async Task<AccountTask> FindTaskByRemoteIdAsync(AccountTask remoteTask)
    {
        if (string.IsNullOrWhiteSpace(remoteTask?.RemoteId))
            return null;

        var listId = remoteTask.TaskListId;
        var remoteId = remoteTask.RemoteId;

        return await Connection.Table<AccountTask>()
            .FirstOrDefaultAsync(task => task.TaskListId == listId && task.RemoteId == remoteId)
            .ConfigureAwait(false);
    }

    private async Task CompleteTaskMutationOnExistingAsync(AccountTask local, AccountTask remoteTask)
    {
        var storedSteps = local.Steps?.ToList() ?? [];

        local.RemoteId = remoteTask?.RemoteId ?? local.RemoteId;
        local.RemoteVersion = remoteTask?.RemoteVersion ?? local.RemoteVersion;
        if (remoteTask is not null)
        {
            // A provider-side move creates the task in another list and removes the source
            // copy. Keep the existing local identity, but adopt the authoritative parent.
            local.TaskListId = remoteTask.TaskListId;
            local.Title = remoteTask.Title;
            local.Notes = remoteTask.Notes;
            local.DueDate = NormalizeDate(remoteTask.DueDate);
            local.IsCompleted = remoteTask.IsCompleted;
            local.CompletedAtUtc = remoteTask.CompletedAtUtc;
            local.RemoteOrder = remoteTask.RemoteOrder;

            // Synchronizers copy this local-only value from the requested snapshot onto the
            // mutation result. Commit it here so My Day survives the provider round trip.
            local.MyDayDateUtc = NormalizeDate(remoteTask.MyDayDateUtc);

            // Only Graph carries importance. Adopting a Google response would clear a local star.
            // Full provider reconciliation preserves both local-only fields separately.
            if (local.SourceKind == TaskSourceKind.Outlook)
                local.IsImportant = remoteTask.IsImportant;
        }
        local.PendingMutation = TaskPendingMutation.None;

        // The row now owns this remote id. Any other row that claimed it came from a
        // reconciliation that raced this mutation, and only one of them may survive.
        var duplicateIds = string.IsNullOrWhiteSpace(local.RemoteId)
            ? []
            : (await Connection.Table<AccountTask>()
                .Where(task => task.TaskListId == local.TaskListId && task.RemoteId == local.RemoteId)
                .ToListAsync().ConfigureAwait(false))
                .Where(task => task.Id != local.Id)
                .Select(task => task.Id)
                .ToList();

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var duplicateId in duplicateIds)
            {
                transaction.Execute("DELETE FROM TaskStep WHERE TaskId = ?", duplicateId);
                transaction.Execute("DELETE FROM TaskCard WHERE Id = ?", duplicateId);
            }

            transaction.Update(local, typeof(AccountTask));
            if (remoteTask is null)
                return;

            var matchedStepIds = new HashSet<Guid>();
            foreach (var remoteStep in remoteTask.Steps ?? [])
            {
                var storedStep = !string.IsNullOrWhiteSpace(remoteStep.RemoteId)
                    ? storedSteps.FirstOrDefault(step => string.Equals(step.RemoteId, remoteStep.RemoteId, StringComparison.Ordinal))
                    : null;
                storedStep ??= remoteStep.Id == Guid.Empty
                    ? null
                    : storedSteps.FirstOrDefault(step => step.Id == remoteStep.Id);

                if (storedStep is null)
                {
                    remoteStep.Id = remoteStep.Id == Guid.Empty ? Guid.NewGuid() : remoteStep.Id;
                    remoteStep.TaskId = local.Id;
                    remoteStep.MailAccountId = local.MailAccountId;
                    remoteStep.SourceKind = local.SourceKind;
                    remoteStep.PendingMutation = TaskPendingMutation.None;
                    remoteStep.CreatedAtUtc = remoteStep.CreatedAtUtc == default ? DateTime.UtcNow : remoteStep.CreatedAtUtc;
                    remoteStep.ModifiedAtUtc = DateTime.UtcNow;
                    transaction.Insert(remoteStep, typeof(AccountTaskStep));
                    matchedStepIds.Add(remoteStep.Id);
                    continue;
                }

                storedStep.RemoteId = remoteStep.RemoteId ?? storedStep.RemoteId;
                storedStep.RemoteVersion = remoteStep.RemoteVersion ?? storedStep.RemoteVersion;
                storedStep.Title = remoteStep.Title ?? storedStep.Title;
                storedStep.IsCompleted = remoteStep.IsCompleted;
                storedStep.Order = remoteStep.Order;
                storedStep.PendingMutation = TaskPendingMutation.None;
                storedStep.ModifiedAtUtc = DateTime.UtcNow;
                transaction.Update(storedStep, typeof(AccountTaskStep));
                matchedStepIds.Add(storedStep.Id);
            }

            // A refreshed Graph task carries the complete checklist. Remove only remote-backed
            // rows that disappeared, and keep local rows that still have a queued mutation.
            foreach (var storedStep in storedSteps)
            {
                if (matchedStepIds.Contains(storedStep.Id) ||
                    storedStep.PendingMutation != TaskPendingMutation.None ||
                    string.IsNullOrWhiteSpace(storedStep.RemoteId))
                {
                    continue;
                }

                transaction.Execute("DELETE FROM TaskStep WHERE Id = ?", storedStep.Id);
            }
        }).ConfigureAwait(false);
    }

    public async Task CompleteStepMutationAsync(Guid stepId, AccountTaskStep remoteStep, bool deleted)
    {
        var local = await Connection.Table<AccountTaskStep>().FirstOrDefaultAsync(step => step.Id == stepId).ConfigureAwait(false);

        if (deleted)
        {
            if (local is not null)
                await Connection.ExecuteAsync("DELETE FROM TaskStep WHERE Id = ?", local.Id).ConfigureAwait(false);
            return;
        }

        if (local is null)
        {
            if (remoteStep is null)
                throw new InvalidOperationException("A successful task-step mutation requires a step snapshot.");

            remoteStep.Id = stepId;
            remoteStep.PendingMutation = TaskPendingMutation.None;
            remoteStep.CreatedAtUtc = remoteStep.CreatedAtUtc == default ? DateTime.UtcNow : remoteStep.CreatedAtUtc;
            remoteStep.ModifiedAtUtc = DateTime.UtcNow;

            await Connection.InsertAsync(remoteStep, typeof(AccountTaskStep)).ConfigureAwait(false);
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

    public async Task DeleteAccountTasksAsync(Guid accountId)
    {
        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM TaskStep WHERE MailAccountId = ?", accountId);
            transaction.Execute("DELETE FROM TaskCard WHERE MailAccountId = ?", accountId);
            transaction.Execute("DELETE FROM TaskList WHERE MailAccountId = ?", accountId);
            transaction.Execute("DELETE FROM TaskListGroup WHERE MailAccountId = ?", accountId);
            transaction.Execute("DELETE FROM TaskSyncState WHERE MailAccountId = ?", accountId);
        }).ConfigureAwait(false);
    }

    public Task DeleteTaskListsBySourceAsync(Guid accountId, TaskSourceKind sourceKind)
        => Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM TaskStep WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
            transaction.Execute("DELETE FROM TaskCard WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
            transaction.Execute("DELETE FROM TaskList WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
            transaction.Execute("DELETE FROM TaskSyncState WHERE MailAccountId = ? AND SourceKind = ?", accountId, sourceKind);
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
