using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Tasks;

public sealed class TaskListPlacementDelta
{
    public string ListRemoteId { get; init; }
    public string GroupRemoteId { get; init; }
    public string RemoteVersion { get; init; }
    public string RemoteOrder { get; init; }
    public string ColorHex { get; init; }
    public TaskSortKind? SortKind { get; init; }
    public bool? SortAscending { get; init; }
    public bool? ShowCompletedTasks { get; init; }
}

/// <summary>A complete, transaction-ready account topology delta.</summary>
public sealed class TaskTopologyDelta
{
    public Guid MailAccountId { get; init; }
    public TaskSourceKind SourceKind { get; init; }
    public IReadOnlyList<AccountTaskListGroup> Groups { get; init; } = [];
    public IReadOnlyCollection<string> DeletedGroupRemoteIds { get; init; } = [];
    public IReadOnlyList<AccountTaskList> Lists { get; init; } = [];
    public IReadOnlyCollection<string> DeletedListRemoteIds { get; init; } = [];
    public IReadOnlyList<TaskListPlacementDelta> Placements { get; init; } = [];
    public bool ReconcileGroups { get; init; }
    public bool ReconcileLists { get; init; }
    public string ListDeltaLink { get; init; }
    public string SubstrateGroupDeltaLink { get; init; }
    public string SubstrateFolderDeltaLink { get; init; }
}

/// <summary>A task-list delta whose data and cursor must commit atomically.</summary>
public sealed class TaskHierarchyDelta
{
    public Guid TaskListId { get; init; }
    public IReadOnlyList<AccountTask> Tasks { get; init; } = [];
    public IReadOnlyCollection<string> DeletedTaskRemoteIds { get; init; } = [];
    public IReadOnlyCollection<string> DeletedStepRemoteIds { get; init; } = [];
    public IReadOnlyCollection<string> AuthoritativeStepParentRemoteIds { get; init; } = [];
    public bool IsFullSnapshot { get; init; }
    public string TaskDeltaLink { get; init; }
    public DateTime? WatermarkUtc { get; init; }
}
