#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>
/// Turns changed VTODO resources into a task hierarchy delta. Every VTODO is a node whose parent
/// is the UID its RELATED-TO names. A node is a step when its parent is in the list and is itself
/// a root; every other node is a task. Deeper nesting is flattened into tasks.
/// </summary>
public sealed partial class CalDavTaskSynchronizationEngine
{
    private async Task<HierarchyPlan> BuildHierarchyAsync(
        Guid accountId,
        AccountTaskList list,
        IReadOnlyList<AccountTask> storedTasks,
        Dictionary<string, TaskResourceShadow> shadows,
        Dictionary<string, AppliedResource> applied,
        HashSet<string> deletedUids)
    {
        var storedTaskByUid = new Dictionary<string, AccountTask>(StringComparer.Ordinal);
        var storedStepByUid = new Dictionary<string, (AccountTaskStep Step, AccountTask Parent)>(StringComparer.Ordinal);

        foreach (var task in storedTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.RemoteId))
                storedTaskByUid.TryAdd(task.RemoteId, task);

            foreach (var step in task.Steps ?? [])
            {
                if (!string.IsNullOrWhiteSpace(step.RemoteId))
                    storedStepByUid.TryAdd(step.RemoteId, (step, task));
            }
        }

        // Stored rows stand in for resources that did not change: a task is a root, a step points
        // at its task. Changed resources say for themselves.
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var uid in storedTaskByUid.Keys)
            parentOf[uid] = null;
        foreach (var (uid, entry) in storedStepByUid)
            parentOf[uid] = string.IsNullOrWhiteSpace(entry.Parent.RemoteId) ? null : entry.Parent.RemoteId;
        foreach (var (uid, resource) in applied)
            parentOf[uid] = resource.Todo.ParentUid;
        foreach (var uid in deletedUids)
            parentOf.Remove(uid);

        bool IsRoot(string uid)
            => !parentOf.TryGetValue(uid, out var parent) ||
               parent == null ||
               string.Equals(parent, uid, StringComparison.Ordinal) ||
               !parentOf.ContainsKey(parent);

        string? FinalParent(string uid)
            => !IsRoot(uid) && parentOf[uid] is { } parent && IsRoot(parent) ? parent : null;

        // A stored step whose parent was removed, or became a step itself, turns into a task. Its
        // fields come from the stored resource because the step row does not hold them.
        foreach (var (uid, entry) in storedStepByUid.ToList())
        {
            if (applied.ContainsKey(uid) || !parentOf.ContainsKey(uid))
                continue;

            if (string.Equals(FinalParent(uid), entry.Parent.RemoteId, StringComparison.Ordinal))
                continue;

            var reloaded = await LoadStoredResourceAsync(accountId, list.Id, entry.Step.Id, uid).ConfigureAwait(false)
                           ?? new AppliedResource(
                               new CalDavTodo { Uid = uid, Summary = entry.Step.Title, Status = entry.Step.IsCompleted ? "COMPLETED" : string.Empty },
                               string.Empty,
                               entry.Step.RemoteVersion ?? string.Empty,
                               string.Empty,
                               IsDownloaded: false);

            applied[uid] = reloaded;
            parentOf[uid] = reloaded.Todo.ParentUid;
        }

        var parents = new List<string>();
        var parentSet = new HashSet<string>(StringComparer.Ordinal);

        void AddParent(string? uid)
        {
            if (!string.IsNullOrWhiteSpace(uid) && parentOf.ContainsKey(uid) && FinalParent(uid) == null && parentSet.Add(uid))
                parents.Add(uid);
        }

        foreach (var uid in applied.Keys)
        {
            AddParent(FinalParent(uid) ?? uid);

            if (storedStepByUid.TryGetValue(uid, out var previous))
                AddParent(previous.Parent.RemoteId);
        }

        foreach (var uid in deletedUids)
        {
            if (storedStepByUid.TryGetValue(uid, out var previous))
                AddParent(previous.Parent.RemoteId);
        }

        var childrenOf = parentOf.Keys
            .Select(uid => (Uid: uid, Parent: FinalParent(uid)))
            .Where(item => item.Parent != null)
            .GroupBy(item => item.Parent!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Uid).ToList(), StringComparer.Ordinal);

        var storedTodoCache = new Dictionary<Guid, CalDavTodo?>();
        var tasks = new List<AccountTask>();
        var authoritativeParents = new List<string>();

        foreach (var parentUid in parents)
        {
            AccountTask? task = applied.TryGetValue(parentUid, out var parentResource)
                ? MapTask(parentResource, list, storedTaskByUid.TryGetValue(parentUid, out var storedParent) ? storedParent.Id : null)
                : storedTaskByUid.TryGetValue(parentUid, out var storedTask) ? CloneStoredTask(storedTask) : null;

            if (task == null)
                continue;

            var entries = new List<(AccountTaskStep Step, CalDavTodo? Todo)>();
            foreach (var childUid in childrenOf.TryGetValue(parentUid, out var children) ? children : [])
            {
                if (applied.TryGetValue(childUid, out var childResource))
                {
                    entries.Add((MapStep(childResource, list, storedStepByUid.TryGetValue(childUid, out var storedChild) ? storedChild.Step.Id : null), childResource.Todo));
                    continue;
                }

                if (!storedStepByUid.TryGetValue(childUid, out var storedStep))
                    continue;

                if (!storedTodoCache.TryGetValue(storedStep.Step.Id, out var storedTodo))
                {
                    storedTodo = (await LoadStoredResourceAsync(accountId, list.Id, storedStep.Step.Id, childUid).ConfigureAwait(false))?.Todo;
                    storedTodoCache[storedStep.Step.Id] = storedTodo;
                }

                entries.Add((CloneStoredStep(storedStep.Step), storedTodo));
            }

            var ordered = entries
                .OrderBy(entry => entry.Todo?.SortOrder.HasValue == true ? 0 : 1)
                .ThenBy(entry => entry.Todo?.SortOrder ?? 0)
                .ThenBy(entry => entry.Todo?.CreatedUtc ?? entry.Todo?.DtStampUtc ?? DateTime.MaxValue)
                .ThenBy(entry => entry.Step.Order)
                .ThenBy(entry => entry.Step.RemoteId, StringComparer.Ordinal)
                .Select(entry => entry.Step)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
                ordered[index].Order = index;

            task.Steps = ordered;
            tasks.Add(task);
            authoritativeParents.Add(parentUid);
        }

        return new HierarchyPlan(
            tasks,
            deletedUids.Where(storedTaskByUid.ContainsKey).ToList(),
            deletedUids.Where(storedStepByUid.ContainsKey).ToList(),
            authoritativeParents);
    }

    /// <summary>
    /// Stores the downloaded resources under the rows that now hold their UIDs and removes the
    /// stored copies of resources that are gone or now belong to another row.
    /// </summary>
    private async Task UpdateShadowsAsync(
        Guid accountId,
        Guid listId,
        Dictionary<string, TaskResourceShadow> shadows,
        Dictionary<string, AppliedResource> applied,
        HashSet<string> deletedKeys,
        HashSet<string> deletedUids)
    {
        var refreshed = await _taskService.GetTasksAsync(accountId, listId).ConfigureAwait(false) ?? [];
        var idsByUid = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var task in refreshed)
        {
            if (!string.IsNullOrWhiteSpace(task.RemoteId))
                idsByUid.TryAdd(task.RemoteId, task.Id);

            foreach (var step in task.Steps ?? [])
            {
                if (!string.IsNullOrWhiteSpace(step.RemoteId))
                    idsByUid.TryAdd(step.RemoteId, step.Id);
            }
        }

        var shadowItemsByUid = shadows.Values
            .SelectMany(shadow => shadow.Items)
            .Where(item => !string.IsNullOrWhiteSpace(item.RemoteEventId))
            .GroupBy(item => item.RemoteEventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var saves = new List<(Guid Id, string Uid, AppliedResource Resource)>();
        var removals = new HashSet<Guid>();

        foreach (var key in deletedKeys)
        {
            if (shadows.TryGetValue(key, out var shadow))
                removals.UnionWith(shadow.Items.Select(item => item.CalendarItemId));
        }

        foreach (var uid in deletedUids)
        {
            if (shadowItemsByUid.TryGetValue(uid, out var items))
                removals.UnionWith(items.Select(item => item.CalendarItemId));
        }

        foreach (var (uid, resource) in applied)
        {
            if (string.IsNullOrWhiteSpace(resource.Href) || string.IsNullOrWhiteSpace(resource.IcsContent) || !idsByUid.TryGetValue(uid, out var id))
                continue;

            saves.Add((id, uid, resource));

            if (shadows.TryGetValue(CalDavSynchronizationEngine.ResourceKey(resource.Href), out var shadow))
                removals.UnionWith(shadow.Items.Where(item => item.CalendarItemId != id).Select(item => item.CalendarItemId));

            if (shadowItemsByUid.TryGetValue(uid, out var items))
                removals.UnionWith(items.Where(item => item.CalendarItemId != id).Select(item => item.CalendarItemId));
        }

        removals.ExceptWith(saves.Select(save => save.Id));

        foreach (var id in removals)
            await _icsFileService.DeleteCalendarItemIcsAsync(accountId, id).ConfigureAwait(false);

        foreach (var (id, uid, resource) in saves)
            await _icsFileService.SaveCalendarItemIcsAsync(accountId, listId, id, uid, resource.Href, resource.ETag, resource.IcsContent).ConfigureAwait(false);
    }

    private async Task<AppliedResource?> LoadStoredResourceAsync(Guid accountId, Guid listId, Guid itemId, string uid)
    {
        var snapshot = await _icsFileService.GetCalendarItemIcsAsync(accountId, listId, itemId).ConfigureAwait(false);
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.IcsContent))
            return null;

        var todo = TaskIcsBuilder.ParseTodos(snapshot.IcsContent).FirstOrDefault(item => string.Equals(item.Uid, uid, StringComparison.Ordinal));
        return todo == null
            ? null
            : new AppliedResource(todo, snapshot.ExactHref, snapshot.ETag ?? string.Empty, snapshot.IcsContent, IsDownloaded: false);
    }

    private static AccountTask MapTask(AppliedResource resource, AccountTaskList list, Guid? existingId)
    {
        var todo = resource.Todo;

        return new AccountTask
        {
            Id = existingId ?? Guid.NewGuid(),
            MailAccountId = list.MailAccountId,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.CalDav,
            RemoteId = todo.Uid,
            RemoteVersion = resource.ETag,
            Title = TitleOf(todo),
            Notes = todo.Description ?? string.Empty,
            DueDate = todo.DueDate,
            IsCompleted = todo.IsCompleted,
            CompletedAtUtc = todo.IsCompleted ? todo.CompletedUtc : null,
            IsImportant = todo.IsImportant,
            CreatedAtUtc = todo.CreatedUtc ?? todo.DtStampUtc ?? default,
            Steps = []
        };
    }

    private static AccountTaskStep MapStep(AppliedResource resource, AccountTaskList list, Guid? existingId)
    {
        var todo = resource.Todo;

        return new AccountTaskStep
        {
            Id = existingId ?? Guid.NewGuid(),
            MailAccountId = list.MailAccountId,
            SourceKind = TaskSourceKind.CalDav,
            RemoteId = todo.Uid,
            RemoteVersion = resource.ETag,
            Title = TitleOf(todo),
            IsCompleted = todo.IsCompleted,
            CreatedAtUtc = todo.CreatedUtc ?? todo.DtStampUtc ?? default
        };
    }

    private static string TitleOf(CalDavTodo todo)
        => string.IsNullOrWhiteSpace(todo.Summary) ? Translator.CalDavTasks_UntitledTask : todo.Summary;

    private static AccountTask CloneStoredTask(AccountTask stored)
        => new()
        {
            Id = stored.Id,
            MailAccountId = stored.MailAccountId,
            TaskListId = stored.TaskListId,
            SourceKind = stored.SourceKind,
            RemoteId = stored.RemoteId,
            RemoteVersion = stored.RemoteVersion,
            Title = stored.Title,
            Notes = stored.Notes,
            DueDate = stored.DueDate,
            IsCompleted = stored.IsCompleted,
            IsImportant = stored.IsImportant,
            MyDayDateUtc = stored.MyDayDateUtc,
            CompletedAtUtc = stored.CompletedAtUtc,
            RemoteOrder = stored.RemoteOrder,
            CreatedAtUtc = stored.CreatedAtUtc,
            Steps = []
        };

    private static AccountTaskStep CloneStoredStep(AccountTaskStep stored)
        => new()
        {
            Id = stored.Id,
            TaskId = stored.TaskId,
            MailAccountId = stored.MailAccountId,
            SourceKind = stored.SourceKind,
            RemoteId = stored.RemoteId,
            RemoteVersion = stored.RemoteVersion,
            Title = stored.Title,
            IsCompleted = stored.IsCompleted,
            Order = stored.Order,
            CreatedAtUtc = stored.CreatedAtUtc
        };

    private sealed record HierarchyPlan(
        List<AccountTask> Tasks,
        List<string> DeletedTaskRemoteIds,
        List<string> DeletedStepRemoteIds,
        List<string> AuthoritativeParentRemoteIds);
}
