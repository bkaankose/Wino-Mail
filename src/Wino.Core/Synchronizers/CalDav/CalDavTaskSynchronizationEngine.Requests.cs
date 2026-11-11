#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>
/// Writes task mutations as VTODO resources: lists are calendar collections (MKCALENDAR,
/// PROPPATCH, DELETE), tasks and steps are resources, a step carrying RELATED-TO;RELTYPE=PARENT.
/// Groups and list placement exist only in Wino. Requests for local lists are completed locally.
/// </summary>
public sealed partial class CalDavTaskSynchronizationEngine
{
    private static readonly string[] TaskComponents = ["VTODO"];

    public async Task ExecuteRequestsAsync(
        MailAccount account,
        IReadOnlyList<ITaskActionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (requests == null || requests.Count == 0)
            return;

        CalDavConnectionSettings? settings = null;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request is not TaskActionRequest taskRequest)
                throw new NotSupportedException($"CalDAV task request {request?.GetType().Name} is not supported.");

            switch (taskRequest.Operation)
            {
                case TaskSynchronizerOperation.CreateGroup:
                case TaskSynchronizerOperation.UpdateGroup:
                case TaskSynchronizerOperation.DeleteGroup:
                    await CompleteGroupAsync(taskRequest).ConfigureAwait(false);
                    continue;
                case TaskSynchronizerOperation.UpdateListPlacement:
                    await CompletePlacementAsync(taskRequest).ConfigureAwait(false);
                    continue;
            }

            if (await IsLocalRequestAsync(taskRequest).ConfigureAwait(false))
            {
                await new LocalTaskSynchronizer(_taskService)
                    .ExecuteRequestsAsync(new ITaskActionRequest[] { taskRequest }, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!IsTaskSourceAvailable(account))
                throw new InvalidOperationException(Translator.Synchronizer_TasksUnavailable);

            settings ??= await CalDavConnection.CreateRequiredSettingsAsync(account, _autoDiscoveryService, cancellationToken).ConfigureAwait(false);
            var context = new RequestContext(account, settings, taskRequest);

            switch (taskRequest.Operation)
            {
                case TaskSynchronizerOperation.CreateList:
                    await CreateListAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.UpdateList:
                    await UpdateListAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.DeleteList:
                    await DeleteListAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.CreateTask:
                    await CreateTaskAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.UpdateTask:
                    await UpdateTaskAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.DeleteTask:
                    await DeleteTaskAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.CreateStep:
                case TaskSynchronizerOperation.UpdateStep:
                    await WriteStepAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.DeleteStep:
                    await DeleteStepAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"CalDAV task operation {taskRequest.Operation} is not supported.");
            }
        }
    }

    /// <summary>The account may keep its local lists next to the CalDAV ones; their requests never reach the server.</summary>
    private async Task<bool> IsLocalRequestAsync(TaskActionRequest request)
    {
        switch (request.Operation)
        {
            case TaskSynchronizerOperation.CreateList:
            case TaskSynchronizerOperation.UpdateList:
            case TaskSynchronizerOperation.DeleteList:
            {
                var list = request.List ?? request.OriginalList;
                if (list == null)
                    return false;

                var stored = await _taskService.GetTaskListAsync(list.Id).ConfigureAwait(false);
                return (stored?.SourceKind ?? list.SourceKind) == TaskSourceKind.Local;
            }
            case TaskSynchronizerOperation.CreateTask:
            case TaskSynchronizerOperation.UpdateTask:
            case TaskSynchronizerOperation.DeleteTask:
            {
                var task = request.Task ?? request.OriginalTask;
                if (task == null)
                    return false;

                var targetKind = await GetListKindAsync(task.TaskListId, task.SourceKind).ConfigureAwait(false);
                var original = request.OriginalTask;
                var sourceKind = original != null && original.TaskListId != Guid.Empty && original.TaskListId != task.TaskListId
                    ? await GetListKindAsync(original.TaskListId, original.SourceKind).ConfigureAwait(false)
                    : targetKind;

                return targetKind == TaskSourceKind.Local && sourceKind == TaskSourceKind.Local;
            }
            case TaskSynchronizerOperation.CreateStep:
            case TaskSynchronizerOperation.UpdateStep:
            case TaskSynchronizerOperation.DeleteStep:
            {
                var step = request.Step ?? request.OriginalStep;
                if (step == null)
                    return false;

                var parent = await _taskService.GetTaskAsync(step.TaskId).ConfigureAwait(false);
                return parent == null
                    ? step.SourceKind == TaskSourceKind.Local
                    : await GetListKindAsync(parent.TaskListId, parent.SourceKind).ConfigureAwait(false) == TaskSourceKind.Local;
            }
            default:
                return false;
        }
    }

    private async Task<TaskSourceKind> GetListKindAsync(Guid listId, TaskSourceKind fallback)
        => (await _taskService.GetTaskListAsync(listId).ConfigureAwait(false))?.SourceKind ?? fallback;

    #region Lists

    private async Task CreateListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var list = context.Request.List ?? throw new InvalidOperationException("The task-list mutation has no list snapshot.");
        var calendarHome = await ResolveCalendarHomeAsync(context.Account.Id, context.Settings, cancellationToken).ConfigureAwait(false);
        string remoteId;

        using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
        {
            remoteId = await _client.CreateCalendarCollectionAsync(
                context.Settings,
                calendarHome,
                Guid.NewGuid().ToString("N"),
                list.Title ?? string.Empty,
                TaskComponents,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(list.ColorHex))
            {
                try
                {
                    await _client.UpdateCalendarCollectionAsync(context.Settings, remoteId, list.Title ?? string.Empty, list.ColorHex, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The collection exists; a color the server does not take stays local.
                    Logger.Warning(ex, "Could not set the color of the new CalDAV task list {ListName}", list.Title);
                }
            }
        }

        var mapped = RequestEntityCloner.TaskList(list);
        mapped.SourceKind = TaskSourceKind.CalDav;
        mapped.RemoteId = remoteId;
        mapped.IsDefault = false;
        mapped.IsReadOnly = false;

        await _taskService.CompleteListMutationAsync(list.Id, mapped, false).ConfigureAwait(false);
    }

    private async Task UpdateListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var list = context.Request.List ?? throw new InvalidOperationException("The task-list mutation has no list snapshot.");
        var stored = await _taskService.GetTaskListAsync(list.Id).ConfigureAwait(false);
        var remoteId = !string.IsNullOrWhiteSpace(stored?.RemoteId) ? stored.RemoteId : list.RemoteId;
        if (string.IsNullOrWhiteSpace(remoteId))
            throw new InvalidOperationException($"Task list {list.Id} is unavailable for update.");

        using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
        {
            await _client.UpdateCalendarCollectionAsync(context.Settings, remoteId, list.Title ?? string.Empty, list.ColorHex, cancellationToken).ConfigureAwait(false);
        }

        var mapped = RequestEntityCloner.TaskList(list);
        mapped.SourceKind = TaskSourceKind.CalDav;
        mapped.RemoteId = remoteId;
        mapped.RemoteVersion = stored?.RemoteVersion ?? list.RemoteVersion;
        mapped.IsDefault = false;
        mapped.IsReadOnly = stored?.IsReadOnly ?? list.IsReadOnly;

        await _taskService.CompleteListMutationAsync(list.Id, mapped, false).ConfigureAwait(false);
    }

    private async Task DeleteListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var list = context.Request.List ?? context.Request.OriginalList
                   ?? throw new InvalidOperationException("The task-list mutation has no list snapshot.");
        var stored = await _taskService.GetTaskListAsync(list.Id).ConfigureAwait(false);
        var remoteId = !string.IsNullOrWhiteSpace(stored?.RemoteId) ? stored.RemoteId : list.RemoteId;

        if (!string.IsNullOrWhiteSpace(remoteId))
        {
            if (stored?.IsSharedCalendarCollection == true || list.IsSharedCalendarCollection)
                throw new InvalidOperationException(Translator.CalDavTasks_SharedCollectionDeleteBlocked);

            using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
            {
                // A row stored before the component set was recorded says nothing, so the server is
                // asked; DELETE on a collection that also holds events would delete the calendar.
                if (await CollectionMayHoldEventsAsync(context, remoteId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException(Translator.CalDavTasks_SharedCollectionDeleteBlocked);

                await _client.DeleteCalendarCollectionAsync(context.Settings, remoteId, cancellationToken).ConfigureAwait(false);
            }
        }

        // Deletes pass the snapshot only because the parameter is not annotated nullable; the service ignores it.
        await _taskService.CompleteListMutationAsync(list.Id, list, true).ConfigureAwait(false);
        await _icsFileService.DeleteCalendarIcsForCalendarAsync(context.Account.Id, list.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// True unless the server lists the collection with a component set that lacks VEVENT. A
    /// collection missing from the listing is gone and holds nothing.
    /// </summary>
    private async Task<bool> CollectionMayHoldEventsAsync(RequestContext context, string remoteId, CancellationToken cancellationToken)
    {
        var collections = await RefreshCollectionsAsync(context.Account.Id, context.Settings, cancellationToken).ConfigureAwait(false);
        var collection = collections.FirstOrDefault(item => string.Equals(item.RemoteCalendarId?.TrimEnd('/'), remoteId.TrimEnd('/'), StringComparison.Ordinal));

        return collection?.SupportsEvents == true;
    }

    private async Task<string> ResolveCalendarHomeAsync(Guid accountId, CalDavConnectionSettings settings, CancellationToken cancellationToken)
    {
        var state = await _stateStore.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
        var home = state.CalendarHomeHrefs.FirstOrDefault(href => !string.IsNullOrWhiteSpace(href));
        if (home != null)
            return home;

        var discovery = await DiscoverAsync(accountId, settings, cancellationToken).ConfigureAwait(false);
        return discovery.CalendarHomeHrefs?.FirstOrDefault(href => !string.IsNullOrWhiteSpace(href))
               ?? throw new InvalidOperationException(Translator.DavError_InvalidResponse);
    }

    private async Task CompleteGroupAsync(TaskActionRequest request)
    {
        var group = request.Group ?? request.OriginalGroup
                    ?? throw new InvalidOperationException("The task-group mutation has no group snapshot.");
        var deleted = request.Operation == TaskSynchronizerOperation.DeleteGroup;

        await _taskService.CompleteTaskListGroupMutationAsync(group.Id, group, deleted).ConfigureAwait(false);
    }

    private async Task CompletePlacementAsync(TaskActionRequest request)
    {
        var list = request.List ?? await _taskService.GetTaskListAsync(request.TaskListId ?? Guid.Empty).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The task-list placement mutation has no list snapshot.");

        await _taskService.CompleteTaskListPlacementMutationAsync(list.Id, list).ConfigureAwait(false);
    }

    #endregion

    #region Tasks

    private async Task CreateTaskAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var task = context.Request.Task ?? throw new InvalidOperationException("The task mutation has no task snapshot.");
        var stored = await _taskService.GetTaskAsync(task.Id).ConfigureAwait(false);
        ApplyStoredTaskIdentity(task, stored);

        var list = await RequireRemoteListAsync(task.TaskListId, context.Request.Operation).ConfigureAwait(false);
        var uid = string.IsNullOrWhiteSpace(task.RemoteId) ? task.Id.ToString("N") : task.RemoteId;
        WrittenResource written;

        using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
        {
            written = await WriteTodoAsync(context.Settings, list, ToContent(task, uid), null, null, cancellationToken).ConfigureAwait(false);
            await SaveShadowAsync(context.Account.Id, list.Id, task.Id, uid, written).ConfigureAwait(false);
        }

        var remoteTask = BuildRemoteTask(task, list, uid, written.ETag, RemoteBackedSteps(stored));
        await _taskService.CompleteTaskMutationAsync(task.Id, remoteTask, false, task, TaskSynchronizerOperation.CreateTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Patches the stored resource. A task moved to another CalDAV list is written there under the
    /// same UID with its steps, then removed from the old collection; a task moved out to a local
    /// list is removed from the server.
    /// </summary>
    private async Task UpdateTaskAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var task = context.Request.Task ?? throw new InvalidOperationException("The task mutation has no task snapshot.");
        var stored = await _taskService.GetTaskAsync(task.Id).ConfigureAwait(false);
        ApplyStoredTaskIdentity(task, stored);

        var target = await _taskService.GetTaskListAsync(task.TaskListId).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Task list {task.TaskListId} is unavailable for {context.Request.Operation}.");
        var original = context.Request.OriginalTask;
        var sourceListId = original != null && original.TaskListId != Guid.Empty ? original.TaskListId : task.TaskListId;
        var source = sourceListId == target.Id ? target : await _taskService.GetTaskListAsync(sourceListId).ConfigureAwait(false);
        var sourceIsRemote = IsRemoteList(source);
        var uid = string.IsNullOrWhiteSpace(task.RemoteId) ? task.Id.ToString("N") : task.RemoteId;

        if (target.SourceKind == TaskSourceKind.Local)
        {
            if (source != null && sourceIsRemote)
            {
                using (await CalDavConnection.LockCalendarAsync(source.Id, cancellationToken).ConfigureAwait(false))
                {
                    await RemoveTaskResourcesAsync(context, source, task, stored, cancellationToken).ConfigureAwait(false);
                }
            }

            var localTask = RequestEntityCloner.Task(task);
            localTask.Steps = RemoteBackedSteps(stored);
            await _taskService.CompleteTaskMutationAsync(task.Id, localTask, false, task, TaskSynchronizerOperation.UpdateTask).ConfigureAwait(false);
            return;
        }

        if (!IsRemoteList(target))
            throw new InvalidOperationException($"Task list {target.Id} is unavailable for {context.Request.Operation}.");

        WrittenResource written;
        List<AccountTaskStep> steps;

        if (source == null || source.Id == target.Id)
        {
            using (await CalDavConnection.LockCalendarAsync(target.Id, cancellationToken).ConfigureAwait(false))
            {
                var snapshot = await LoadSnapshotAsync(context, target, task.Id, uid, cancellationToken).ConfigureAwait(false);
                var content = ToContent(task, uid, ChangedFields(original, task));
                written = await WriteTodoAsync(context.Settings, target, content, snapshot, snapshot?.IcsContent, cancellationToken).ConfigureAwait(false);
                await SaveShadowAsync(context.Account.Id, target.Id, task.Id, uid, written).ConfigureAwait(false);
            }

            steps = RemoteBackedSteps(stored);
        }
        else
        {
            using (await LockListsAsync(source.Id, target.Id, cancellationToken).ConfigureAwait(false))
            {
                (written, steps) = await MoveTaskResourcesAsync(context, task, uid, source, sourceIsRemote, target, stored, cancellationToken).ConfigureAwait(false);
            }
        }

        var remoteTask = BuildRemoteTask(task, target, uid, written.ETag, steps);
        ApplyAdvancedRecurrence(remoteTask, written, uid);
        await _taskService.CompleteTaskMutationAsync(task.Id, remoteTask, false, task, TaskSynchronizerOperation.UpdateTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a task and its steps to another collection. Every copy is written to the target before
    /// any source copy is deleted, and the stored shadows change only once both stages succeeded.
    /// A failure restores the deleted source copies and removes the target copies, so the server is
    /// left as it was and the next sync does not import the task twice. Steps of a task that came
    /// from a local list are created; steps still waiting for their own create request in a CalDAV
    /// list are left to that request.
    /// </summary>
    private async Task<(WrittenResource Parent, List<AccountTaskStep> Steps)> MoveTaskResourcesAsync(
        RequestContext context,
        AccountTask task,
        string uid,
        AccountTaskList source,
        bool sourceIsRemote,
        AccountTaskList target,
        AccountTask? stored,
        CancellationToken cancellationToken)
    {
        var parentSnapshot = sourceIsRemote
            ? await LoadSnapshotAsync(context, source, task.Id, uid, cancellationToken).ConfigureAwait(false)
            : null;

        var stepPlans = new List<(AccountTaskStep Step, string Uid, CalDavResourceSnapshot? Snapshot)>();
        foreach (var step in stored?.Steps ?? [])
        {
            if (sourceIsRemote && string.IsNullOrWhiteSpace(step.RemoteId))
                continue;

            var stepUid = string.IsNullOrWhiteSpace(step.RemoteId) ? step.Id.ToString("N") : step.RemoteId;
            var stepSnapshot = sourceIsRemote
                ? await LoadSnapshotAsync(context, source, step.Id, stepUid, cancellationToken).ConfigureAwait(false)
                : null;
            stepPlans.Add((step, stepUid, stepSnapshot));
        }

        var writtenTargets = new List<WrittenResource>();
        var deletedSources = new List<(string Uid, CalDavResourceSnapshot Snapshot)>();
        WrittenResource parentWritten;
        var stepWrites = new List<(AccountTaskStep Step, string Uid, WrittenResource Written)>();

        try
        {
            parentWritten = await WriteTodoAsync(context.Settings, target, ToContent(task, uid), null, parentSnapshot?.IcsContent, cancellationToken).ConfigureAwait(false);
            writtenTargets.Add(parentWritten);

            foreach (var (step, stepUid, stepSnapshot) in stepPlans)
            {
                var stepWritten = await WriteTodoAsync(context.Settings, target, ToContent(step, stepUid, uid), null, stepSnapshot?.IcsContent, cancellationToken).ConfigureAwait(false);
                writtenTargets.Add(stepWritten);
                stepWrites.Add((step, stepUid, stepWritten));
            }

            foreach (var (_, stepUid, stepSnapshot) in stepPlans)
            {
                if (stepSnapshot == null)
                    continue;

                await DeleteRemoteAsync(context.Settings, stepSnapshot.ExactHref, stepSnapshot.ETag, cancellationToken).ConfigureAwait(false);
                deletedSources.Add((stepUid, stepSnapshot));
            }

            if (parentSnapshot != null)
            {
                await DeleteRemoteAsync(context.Settings, parentSnapshot.ExactHref, parentSnapshot.ETag, cancellationToken).ConfigureAwait(false);
                deletedSources.Add((uid, parentSnapshot));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Moving CalDAV task {Uid} from {Source} to {Target} failed; restoring the server copies", uid, source.Title, target.Title);
            await RollBackMoveAsync(context.Settings, source, writtenTargets, deletedSources).ConfigureAwait(false);
            throw;
        }

        // The old copy goes first: the store deletes an item by id in whichever list folder it finds first.
        await _icsFileService.DeleteCalendarItemIcsAsync(context.Account.Id, task.Id).ConfigureAwait(false);
        await SaveShadowAsync(context.Account.Id, target.Id, task.Id, uid, parentWritten).ConfigureAwait(false);

        var moved = new List<AccountTaskStep>();
        foreach (var (step, stepUid, stepWritten) in stepWrites)
        {
            await _icsFileService.DeleteCalendarItemIcsAsync(context.Account.Id, step.Id).ConfigureAwait(false);
            await SaveShadowAsync(context.Account.Id, target.Id, step.Id, stepUid, stepWritten).ConfigureAwait(false);

            var remoteStep = RequestEntityCloner.TaskStep(step);
            remoteStep.RemoteId = stepUid;
            remoteStep.RemoteVersion = KnownETag(stepWritten.ETag);
            remoteStep.SourceKind = TaskSourceKind.CalDav;
            moved.Add(remoteStep);
        }

        return (parentWritten, moved);
    }

    /// <summary>
    /// Undoes a partly applied move: deleted source copies are written back first, so the task is
    /// never missing from both collections, then the target copies are removed. Runs uncancelled
    /// because a cancelled move must not be left half done.
    /// </summary>
    private async Task RollBackMoveAsync(
        CalDavConnectionSettings settings,
        AccountTaskList source,
        IReadOnlyList<WrittenResource> writtenTargets,
        IReadOnlyList<(string Uid, CalDavResourceSnapshot Snapshot)> deletedSources)
    {
        foreach (var (sourceUid, snapshot) in deletedSources)
        {
            try
            {
                await _client.UpsertCalendarEventAsync(
                    settings,
                    new CalDavCalendar { RemoteCalendarId = source.RemoteId },
                    new CalDavWriteRequest
                    {
                        RemoteEventId = sourceUid,
                        ExactHref = snapshot.ExactHref,
                        ETag = string.Empty,
                        IcsContent = snapshot.IcsContent,
                        CreateOnly = true
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not restore CalDAV task resource {Href} after a failed move", snapshot.ExactHref);
            }
        }

        foreach (var written in writtenTargets)
        {
            try
            {
                await DeleteRemoteAsync(settings, written.Href, written.ETag, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not remove CalDAV task resource {Href} after a failed move", written.Href);
            }
        }
    }

    private async Task DeleteTaskAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var task = context.Request.Task ?? context.Request.OriginalTask
                   ?? throw new InvalidOperationException("The task mutation has no task snapshot.");
        var stored = await _taskService.GetTaskAsync(task.Id).ConfigureAwait(false);
        ApplyStoredTaskIdentity(task, stored);

        var list = await _taskService.GetTaskListAsync(task.TaskListId).ConfigureAwait(false);
        if (list != null && IsRemoteList(list))
        {
            using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
            {
                await RemoveTaskResourcesAsync(context, list, task, stored, cancellationToken).ConfigureAwait(false);
            }
        }

        await _taskService.CompleteTaskMutationAsync(task.Id, task, true, task, TaskSynchronizerOperation.DeleteTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a task's resource and the resources of its steps. Deleting a task removes its step
    /// rows before the request runs, so steps are also found through their stored RELATED-TO.
    /// </summary>
    private async Task RemoveTaskResourcesAsync(
        RequestContext context,
        AccountTaskList list,
        AccountTask task,
        AccountTask? stored,
        CancellationToken cancellationToken)
    {
        var accountId = context.Account.Id;
        var uid = task.RemoteId;
        var stepIds = new HashSet<Guid>(
            (task.Steps ?? [])
                .Concat(context.Request.OriginalTask?.Steps ?? [])
                .Concat(stored?.Steps ?? [])
                .Select(step => step.Id));

        if (!string.IsNullOrWhiteSpace(uid))
        {
            var liveTasks = await _taskService.GetTasksAsync(accountId, list.Id).ConfigureAwait(false) ?? [];
            var liveIds = new HashSet<Guid>(liveTasks.Select(item => item.Id).Concat(liveTasks.SelectMany(item => item.Steps ?? []).Select(step => step.Id)));

            foreach (var shadow in await _icsFileService.GetCalendarIcsShadowsAsync(accountId, list.Id).ConfigureAwait(false) ?? Array.Empty<CalendarIcsShadow>())
            {
                if (shadow.CalendarItemId == task.Id || stepIds.Contains(shadow.CalendarItemId) || liveIds.Contains(shadow.CalendarItemId))
                    continue;

                var orphan = await LoadStoredResourceAsync(accountId, list.Id, shadow.CalendarItemId, shadow.RemoteEventId).ConfigureAwait(false);
                if (string.Equals(orphan?.Todo.ParentUid, uid, StringComparison.Ordinal))
                    stepIds.Add(shadow.CalendarItemId);
            }
        }

        foreach (var stepId in stepIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _icsFileService.GetCalendarItemIcsAsync(accountId, list.Id, stepId).ConfigureAwait(false);
            if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ExactHref))
                await DeleteRemoteAsync(context.Settings, snapshot.ExactHref, snapshot.ETag, cancellationToken).ConfigureAwait(false);

            await _icsFileService.DeleteCalendarItemIcsAsync(accountId, stepId).ConfigureAwait(false);
        }

        var taskSnapshot = await LoadSnapshotAsync(context, list, task.Id, uid, cancellationToken).ConfigureAwait(false);
        if (taskSnapshot != null)
            await DeleteRemoteAsync(context.Settings, taskSnapshot.ExactHref, taskSnapshot.ETag, cancellationToken).ConfigureAwait(false);

        await _icsFileService.DeleteCalendarItemIcsAsync(accountId, task.Id).ConfigureAwait(false);
    }

    #endregion

    #region Steps

    private async Task WriteStepAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var operation = context.Request.Operation;
        var step = context.Request.Step ?? throw new InvalidOperationException("The step mutation has no step snapshot.");
        var parent = await _taskService.GetTaskAsync(step.TaskId).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Task {step.TaskId} is unavailable for {operation}.");

        if (string.IsNullOrWhiteSpace(parent.RemoteId))
            throw new InvalidOperationException($"Task {parent.Id} has no CalDAV identity for {operation}.");

        ApplyStoredStepIdentity(step, parent.Steps?.FirstOrDefault(item => item.Id == step.Id));

        var list = await RequireRemoteListAsync(parent.TaskListId, operation).ConfigureAwait(false);
        var uid = string.IsNullOrWhiteSpace(step.RemoteId) ? step.Id.ToString("N") : step.RemoteId;
        WrittenResource written;

        using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
        {
            var snapshot = operation == TaskSynchronizerOperation.UpdateStep
                ? await LoadSnapshotAsync(context, list, step.Id, uid, cancellationToken).ConfigureAwait(false)
                : null;

            var fields = operation == TaskSynchronizerOperation.UpdateStep ? ChangedFields(context.Request.OriginalStep, step) : CalDavTodoFields.All;
            written = await WriteTodoAsync(context.Settings, list, ToContent(step, uid, parent.RemoteId, fields), snapshot, snapshot?.IcsContent, cancellationToken).ConfigureAwait(false);
            await SaveShadowAsync(context.Account.Id, list.Id, step.Id, uid, written).ConfigureAwait(false);
        }

        var remoteStep = RequestEntityCloner.TaskStep(step);
        remoteStep.TaskId = parent.Id;
        remoteStep.RemoteId = uid;
        remoteStep.RemoteVersion = KnownETag(written.ETag);
        remoteStep.SourceKind = TaskSourceKind.CalDav;

        if (remoteStep.IsCompleted && FindWrittenTodo(written, uid) is { IsCompleted: false })
            remoteStep.IsCompleted = false;

        await _taskService.CompleteStepMutationAsync(step.Id, remoteStep, false, step, operation).ConfigureAwait(false);
    }

    private async Task DeleteStepAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var step = context.Request.Step ?? context.Request.OriginalStep
                   ?? throw new InvalidOperationException("The step mutation has no step snapshot.");
        var parent = await _taskService.GetTaskAsync(step.TaskId).ConfigureAwait(false);
        var list = parent == null ? null : await _taskService.GetTaskListAsync(parent.TaskListId).ConfigureAwait(false);

        if (list != null && IsRemoteList(list))
        {
            using (await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false))
            {
                var snapshot = await LoadSnapshotAsync(context, list, step.Id, step.RemoteId, cancellationToken).ConfigureAwait(false);
                if (snapshot != null)
                    await DeleteRemoteAsync(context.Settings, snapshot.ExactHref, snapshot.ETag, cancellationToken).ConfigureAwait(false);
            }
        }

        await _taskService.CompleteStepMutationAsync(step.Id, step, true, step, TaskSynchronizerOperation.DeleteStep).ConfigureAwait(false);
        await _icsFileService.DeleteCalendarItemIcsAsync(context.Account.Id, step.Id).ConfigureAwait(false);
    }

    #endregion

    #region Resources

    /// <summary>
    /// PUTs a VTODO. With <paramref name="existing"/> the stored resource is replaced under If-Match;
    /// without it the resource is created under If-None-Match. <paramref name="baseIcs"/> is patched
    /// so properties Wino does not own survive. An unchanged resource is not sent again. When
    /// another client changed the resource first (412), the current copy is downloaded and the
    /// changed fields are patched into it once more.
    /// </summary>
    private async Task<WrittenResource> WriteTodoAsync(
        CalDavConnectionSettings settings,
        AccountTaskList list,
        CalDavTodoContent content,
        CalDavResourceSnapshot? existing,
        string? baseIcs,
        CancellationToken cancellationToken,
        bool isRetry = false)
    {
        var now = DateTime.UtcNow;
        var patched = string.IsNullOrWhiteSpace(baseIcs) ? null : TaskIcsBuilder.PatchTodo(baseIcs, content, now);
        var icsContent = patched ?? TaskIcsBuilder.BuildTodo(content, now);

        if (existing != null && patched != null && ReferenceEquals(patched, baseIcs))
            return new WrittenResource(existing.ExactHref, existing.ETag, patched);

        CalDavWriteResult result;
        try
        {
            result = await _client.UpsertCalendarEventAsync(
                settings,
                new CalDavCalendar { RemoteCalendarId = list.RemoteId },
                new CalDavWriteRequest
                {
                    RemoteEventId = content.Uid,
                    ExactHref = existing?.ExactHref ?? string.Empty,
                    ETag = existing?.ETag ?? string.Empty,
                    IcsContent = icsContent,
                    CreateOnly = existing == null
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (existing == null && ex.StatusCode == 412 && !isRetry)
        {
            // A create whose response was lost leaves the resource behind; the same VTODO is adopted.
            var current = await _client.GetEventResourceAsync(settings, BuildResourceHref(list.RemoteId, content.Uid), cancellationToken).ConfigureAwait(false);
            if (current == null || current.IsDeleted || !TaskIcsBuilder.ParseTodos(current.IcsContent).Any(todo => string.Equals(todo.Uid, content.Uid, StringComparison.Ordinal)))
                throw;

            return await WriteTodoAsync(settings, list, content, ToSnapshot(current), current.IcsContent, cancellationToken, isRetry: true).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (existing != null && ex.StatusCode == 412 && !isRetry)
        {
            var current = await _client.GetEventResourceAsync(settings, existing.ExactHref, cancellationToken).ConfigureAwait(false);
            if (current == null || current.IsDeleted || string.IsNullOrWhiteSpace(current.IcsContent))
            {
                Logger.Information("CalDAV task resource {Href} was removed by another client; writing it again", existing.ExactHref);
                return await WriteTodoAsync(settings, list, content, null, null, cancellationToken, isRetry: true).ConfigureAwait(false);
            }

            Logger.Information("CalDAV task resource {Href} was changed by another client; applying the edit to the current copy", existing.ExactHref);
            return await WriteTodoAsync(settings, list, content, ToSnapshot(current), current.IcsContent, cancellationToken, isRetry: true).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (ex.StatusCode is 400 or 415 or 422)
        {
            // sabre/dav answers 415 for calendar data it does not accept; "invalid response" would mislead.
            throw new DavRequestException(ex.StatusCode, Translator.CalDavTasks_ChangeRejected, ex.ErrorNames, ex.RetryAfter, ex);
        }

        var href = result.ExactHref;
        var eTag = StrongETag(result.ETag);

        if (result.RequiresRefetch)
        {
            var fetched = await _client.GetEventResourceAsync(settings, href, cancellationToken).ConfigureAwait(false);
            if (fetched != null && !fetched.IsDeleted)
            {
                eTag = StrongETag(fetched.ETag);
                if (!string.IsNullOrWhiteSpace(fetched.IcsContent))
                    icsContent = fetched.IcsContent;
            }
        }

        return new WrittenResource(href, eTag, icsContent);
    }

    /// <summary>
    /// The stored copy of an item's resource. Without one (or without its ETag) the resource is
    /// read from the server at the stored href, or at the href Wino gives the UID.
    /// </summary>
    private async Task<CalDavResourceSnapshot?> LoadSnapshotAsync(
        RequestContext context,
        AccountTaskList list,
        Guid itemId,
        string? uid,
        CancellationToken cancellationToken)
    {
        var snapshot = await _icsFileService.GetCalendarItemIcsAsync(context.Account.Id, list.Id, itemId).ConfigureAwait(false);
        if (snapshot != null &&
            !string.IsNullOrWhiteSpace(snapshot.ExactHref) &&
            !string.IsNullOrWhiteSpace(snapshot.IcsContent) &&
            !string.IsNullOrWhiteSpace(snapshot.ETag))
        {
            return snapshot;
        }

        var href = !string.IsNullOrWhiteSpace(snapshot?.ExactHref)
            ? snapshot.ExactHref
            : !string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(list.RemoteId) ? BuildResourceHref(list.RemoteId, uid) : null;

        if (href == null)
            return null;

        CalDavResource? fetched;
        try
        {
            fetched = await _client.GetEventResourceAsync(context.Settings, href, cancellationToken).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (ex.StatusCode is 404 or 410)
        {
            Logger.Information("CalDAV task resource {Href} is gone ({StatusCode})", href, ex.StatusCode);
            return null;
        }

        if (fetched == null || fetched.IsDeleted || string.IsNullOrWhiteSpace(fetched.IcsContent))
            return null;

        if (!string.IsNullOrWhiteSpace(uid) && !TaskIcsBuilder.ParseTodos(fetched.IcsContent).Any(todo => string.Equals(todo.Uid, uid, StringComparison.Ordinal)))
            return null;

        return ToSnapshot(fetched);
    }

    /// <summary>A downloaded resource as a write base; an ETag that cannot be matched becomes "*".</summary>
    private static CalDavResourceSnapshot ToSnapshot(CalDavResource resource)
    {
        var eTag = StrongETag(resource.ETag);
        return new CalDavResourceSnapshot
        {
            ExactHref = resource.ExactHref,
            ETag = string.IsNullOrEmpty(eTag) ? "*" : eTag,
            IcsContent = resource.IcsContent
        };
    }

    /// <summary>
    /// A weak ETag (RFC 9110 section 8.8.1) never matches If-Match, which uses the strong
    /// comparison, so it is treated as unknown.
    /// </summary>
    internal static string StrongETag(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? string.Empty : trimmed;
    }

    /// <summary>Deletes under If-Match; an unknown ETag deletes whatever version is there. A gone resource counts as deleted.</summary>
    private Task DeleteRemoteAsync(CalDavConnectionSettings settings, string href, string? eTag, CancellationToken cancellationToken)
        => _client.DeleteCalendarEventAsync(settings, href, StrongETag(eTag) is { Length: > 0 } strong ? strong : "*", cancellationToken);

    private Task SaveShadowAsync(Guid accountId, Guid listId, Guid itemId, string uid, WrittenResource written)
        => _icsFileService.SaveCalendarItemIcsAsync(accountId, listId, itemId, uid, written.Href, KnownETag(written.ETag), written.IcsContent);

    /// <summary>The href <see cref="ICalDavClient.UpsertCalendarEventAsync"/> gives a new resource: "&lt;collection&gt;/&lt;uid&gt;.ics".</summary>
    private static string BuildResourceHref(string remoteCalendarId, string uid)
        => new Uri(new Uri($"{remoteCalendarId.TrimEnd('/')}/", UriKind.Absolute), $"{Uri.EscapeDataString(uid)}.ics").AbsoluteUri;

    private async Task<AccountTaskList> RequireRemoteListAsync(Guid listId, TaskSynchronizerOperation operation)
    {
        var list = await _taskService.GetTaskListAsync(listId).ConfigureAwait(false);
        return IsRemoteList(list)
            ? list!
            : throw new InvalidOperationException($"Task list {listId} is unavailable for {operation}.");
    }

    private static bool IsRemoteList(AccountTaskList? list)
        => list is { SourceKind: TaskSourceKind.CalDav } && !string.IsNullOrWhiteSpace(list.RemoteId);

    private static async Task<IDisposable> LockListsAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        if (first == second)
            return await CalDavConnection.LockCalendarAsync(first, cancellationToken).ConfigureAwait(false);

        // A fixed order keeps two moves in opposite directions from waiting on each other.
        var (lower, higher) = first.CompareTo(second) < 0 ? (first, second) : (second, first);
        var lowerLock = await CalDavConnection.LockCalendarAsync(lower, cancellationToken).ConfigureAwait(false);
        try
        {
            var higherLock = await CalDavConnection.LockCalendarAsync(higher, cancellationToken).ConfigureAwait(false);
            return new CompositeLock(lowerLock, higherLock);
        }
        catch
        {
            lowerLock.Dispose();
            throw;
        }
    }

    #endregion

    #region Mapping

    private static CalDavTodoContent ToContent(AccountTask task, string uid, CalDavTodoFields fields = CalDavTodoFields.All)
        => new()
        {
            Fields = fields,
            Uid = uid,
            Title = task.Title ?? string.Empty,
            Notes = task.Notes,
            DueDate = task.DueDate?.Date,
            IsCompleted = task.IsCompleted,
            CompletedAtUtc = task.CompletedAtUtc,
            IsImportant = task.IsImportant,
            CreatedAtUtc = task.CreatedAtUtc
        };

    private static CalDavTodoContent ToContent(AccountTaskStep step, string uid, string parentUid, CalDavTodoFields fields = CalDavTodoFields.All)
        => new()
        {
            Fields = fields,
            Uid = uid,
            Title = step.Title ?? string.Empty,
            IsCompleted = step.IsCompleted,
            CreatedAtUtc = step.CreatedAtUtc,
            ParentUid = parentUid,
            IsStep = true
        };

    /// <summary>
    /// What an update changed, so a patch leaves fields another client edited meanwhile alone.
    /// Without the state before the edit every field is written.
    /// </summary>
    private static CalDavTodoFields ChangedFields(AccountTask? original, AccountTask edited)
    {
        if (original == null)
            return CalDavTodoFields.All;

        var fields = CalDavTodoFields.None;
        if (!string.Equals(original.Title ?? string.Empty, edited.Title ?? string.Empty, StringComparison.Ordinal))
            fields |= CalDavTodoFields.Title;
        if (!string.Equals(original.Notes ?? string.Empty, edited.Notes ?? string.Empty, StringComparison.Ordinal))
            fields |= CalDavTodoFields.Notes;
        if (original.DueDate?.Date != edited.DueDate?.Date)
            fields |= CalDavTodoFields.Due;
        if (original.IsCompleted != edited.IsCompleted)
            fields |= CalDavTodoFields.Completion;
        if (original.IsImportant != edited.IsImportant)
            fields |= CalDavTodoFields.Importance;

        return fields;
    }

    private static CalDavTodoFields ChangedFields(AccountTaskStep? original, AccountTaskStep edited)
    {
        if (original == null)
            return CalDavTodoFields.All;

        var fields = CalDavTodoFields.None;
        if (!string.Equals(original.Title ?? string.Empty, edited.Title ?? string.Empty, StringComparison.Ordinal))
            fields |= CalDavTodoFields.Title;
        if (original.IsCompleted != edited.IsCompleted)
            fields |= CalDavTodoFields.Completion;

        return fields;
    }

    private static CalDavTodo? FindWrittenTodo(WrittenResource written, string uid)
        => TaskIcsBuilder.ParseTodos(written.IcsContent).FirstOrDefault(todo => string.Equals(todo.Uid, uid, StringComparison.Ordinal));

    /// <summary>
    /// Completing a recurring VTODO moves it to its next occurrence instead (see
    /// <see cref="TaskIcsBuilder.PatchTodo"/>); the task then stays open with the new due day.
    /// </summary>
    private static void ApplyAdvancedRecurrence(AccountTask remoteTask, WrittenResource written, string uid)
    {
        if (!remoteTask.IsCompleted || FindWrittenTodo(written, uid) is not { IsCompleted: false } todo)
            return;

        remoteTask.IsCompleted = false;
        remoteTask.CompletedAtUtc = null;
        remoteTask.DueDate = todo.DueDate;
    }

    private static AccountTask BuildRemoteTask(AccountTask task, AccountTaskList list, string uid, string eTag, List<AccountTaskStep> steps)
    {
        var remote = RequestEntityCloner.Task(task);
        remote.TaskListId = list.Id;
        remote.SourceKind = TaskSourceKind.CalDav;
        remote.RemoteId = uid;
        remote.RemoteVersion = KnownETag(eTag);
        remote.Steps = steps;
        return remote;
    }

    /// <summary>
    /// The steps a mutation result must carry: completing a task removes every remote-backed step
    /// the result leaves out, and adopts the fields of the steps it lists. Steps with their own
    /// queued request are left out so their pending state survives.
    /// </summary>
    private static List<AccountTaskStep> RemoteBackedSteps(AccountTask? stored)
        => (stored?.Steps ?? [])
            .Where(step => step.PendingMutation == TaskPendingMutation.None && !string.IsNullOrWhiteSpace(step.RemoteId))
            .Select(RequestEntityCloner.TaskStep)
            .ToList();

    private static void ApplyStoredTaskIdentity(AccountTask requested, AccountTask? stored)
    {
        if (stored == null)
            return;

        requested.RemoteId = stored.RemoteId ?? requested.RemoteId;
        requested.RemoteVersion = stored.RemoteVersion ?? requested.RemoteVersion;
    }

    private static void ApplyStoredStepIdentity(AccountTaskStep requested, AccountTaskStep? stored)
    {
        if (stored == null)
            return;

        requested.RemoteId = stored.RemoteId ?? requested.RemoteId;
        requested.RemoteVersion = stored.RemoteVersion ?? requested.RemoteVersion;
    }

    /// <summary>"*" stands in for an ETag the server did not send; it is never stored.</summary>
    private static string KnownETag(string? value) => string.IsNullOrWhiteSpace(value) || value == "*" ? string.Empty : value;

    #endregion

    private sealed record RequestContext(MailAccount Account, CalDavConnectionSettings Settings, TaskActionRequest Request);

    private sealed record WrittenResource(string Href, string ETag, string IcsContent);

    private sealed class CompositeLock(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            second.Dispose();
            first.Dispose();
        }
    }
}
