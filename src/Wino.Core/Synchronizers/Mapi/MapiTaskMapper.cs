#nullable enable annotations
using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mapi;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>A tasks-folder row to the local task, and a local task to the fields written back.</summary>
internal static class MapiTaskMapper
{
    internal static AccountTask ToAccountTask(MapiTaskInfo row, string remoteId, AccountTaskList list) => new()
    {
        Id = Guid.NewGuid(),
        MailAccountId = list.MailAccountId,
        TaskListId = list.Id,
        SourceKind = TaskSourceKind.Exchange,
        RemoteId = remoteId,
        Title = row.Subject ?? string.Empty,
        Notes = string.IsNullOrWhiteSpace(row.Body) ? null : row.Body,
        DueDate = row.DueDate?.Date,
        // PidTagImportance: 0 low, 1 normal, 2 high. The local model keeps one flag.
        IsImportant = row.Importance == 2,
        IsCompleted = row.Completed,
        CompletedAtUtc = row.Completed ? row.CompletedDate : null,
        PendingMutation = TaskPendingMutation.None
    };

    internal static MapiTaskWrite ToWrite(AccountTask item) => new()
    {
        Subject = item.Title,
        Body = item.Notes,
        DueDate = item.DueDate,
        Importance = item.IsImportant ? 2u : 1u,
        IsComplete = item.IsCompleted,
        CompletedDate = item.IsCompleted ? item.CompletedAtUtc ?? DateTime.UtcNow : null
    };
}
