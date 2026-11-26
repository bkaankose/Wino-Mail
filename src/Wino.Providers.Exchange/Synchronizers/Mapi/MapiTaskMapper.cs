using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;
using Wino.Providers.Exchange.MAPI;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

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
        StartDate = row.StartDate?.Date,
        // PidTagImportance: 0 low, 1 normal, 2 high. The local model keeps one flag; the write keeps a low one low.
        IsImportant = row.Importance == MapiTaskOperations.ImportanceHigh,
        Priority = row.Importance switch
        {
            MapiTaskOperations.ImportanceHigh => TaskPriority.High,
            MapiTaskOperations.ImportanceLow => TaskPriority.Low,
            MapiTaskOperations.ImportanceNormal => TaskPriority.Normal,
            _ => TaskPriority.None
        },
        Categories = row.Categories is { Length: > 0 } categories ? TaskFields.JoinCategories(categories) : null,
        ReminderAtUtc = row.ReminderSet == true && row.ReminderTime is { } reminder ? DateTime.SpecifyKind(reminder, DateTimeKind.Utc) : null,
        IsCompleted = row.Completed,
        CompletedAtUtc = row.Completed ? row.CompletedDate : null,
        PendingMutation = TaskPendingMutation.None
    };

    private static TaskCapabilities Capabilities => TaskCapabilities.ForSource(TaskSourceKind.Exchange, ExchangeTransport.MapiHttp);

    internal static MapiTaskWrite ToWrite(AccountTask item) => new()
    {
        WritesStartDate = Capabilities.SupportsStartDate,
        WritesReminder = Capabilities.SupportsReminder,
        WritesCategories = Capabilities.SupportsCategories,
        Subject = item.Title,
        Body = item.Notes,
        DueDate = item.DueDate,
        StartDate = item.StartDate,
        Importance = TaskFields.GetEffectivePriority(item) switch
        {
            TaskPriority.High => MapiTaskOperations.ImportanceHigh,
            TaskPriority.Low => MapiTaskOperations.ImportanceLow,
            _ => MapiTaskOperations.ImportanceNormal
        },
        IsComplete = item.IsCompleted,
        CompletedDate = item.IsCompleted ? item.CompletedAtUtc ?? DateTime.UtcNow : null,
        ReminderAtUtc = item.ReminderAtUtc,
        Categories = [.. TaskFields.GetCategories(item)]
    };
}
