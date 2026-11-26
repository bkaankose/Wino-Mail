using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;

namespace Wino.Mail.ViewModels.Helpers;

/// <summary>
/// The requests that complete or reopen a task. Completing a recurring task whose server does not move it
/// to its next occurrence (Google Tasks, Exchange, local lists) also creates that occurrence: the completed
/// task stops repeating and a copy with the next due date carries the rule on. Both requests go out
/// together; when only the create fails, the completed task stays completed.
/// </summary>
public static class TaskCompletionPlanner
{
    public sealed record Plan(TaskActionRequest Completion, TaskActionRequest NextOccurrence);

    public static async Task<Plan> BuildAsync(AccountTask task, bool isCompleted, ITaskQueryService taskQueryService, bool allowUndo = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        // Capabilities depend on the list's account and, for Exchange, its transport, which only the service knows.
        ArgumentNullException.ThrowIfNull(taskQueryService);

        var original = RequestEntityCloner.Task(task);
        var desired = RequestEntityCloner.Task(task);
        desired.IsCompleted = isCompleted;
        desired.CompletedAtUtc = isCompleted ? DateTime.UtcNow : null;

        TaskActionRequest next = null;
        if (isCompleted && !task.IsCompleted && task.DueDate is { } due && TaskRecurrenceRule.Parse(task.Recurrence) is { } rule)
        {
            var capabilities = await taskQueryService.GetTaskCapabilitiesAsync(task.TaskListId).ConfigureAwait(false);

            if (!capabilities.ServerAdvancesRecurrence && rule.NextOccurrence(due) is { } nextDue)
            {
                desired.Recurrence = null;
                next = new TaskActionRequest(task.MailAccountId, TaskSynchronizerOperation.CreateTask, Task: CreateNextOccurrence(task, rule, nextDue))
                {
                    AllowUndo = allowUndo
                };
            }
        }

        var completion = new TaskActionRequest(desired.MailAccountId, TaskSynchronizerOperation.UpdateTask, Task: desired, OriginalTask: original)
        {
            AllowUndo = allowUndo
        };

        return new Plan(completion, next);
    }

    public static IReadOnlyList<TaskActionRequest> Requests(Plan plan)
        => plan.NextOccurrence is null ? [plan.Completion] : [plan.Completion, plan.NextOccurrence];

    private static AccountTask CreateNextOccurrence(AccountTask task, TaskRecurrenceRule rule, DateTime nextDue)
    {
        var shift = nextDue.Date - task.DueDate!.Value.Date;
        var next = new AccountTask
        {
            MailAccountId = task.MailAccountId,
            TaskListId = task.TaskListId,
            SourceKind = task.SourceKind,
            Title = task.Title,
            Notes = task.Notes,
            DueDate = nextDue,
            IsImportant = task.IsImportant,
            Priority = task.Priority,
            Categories = task.Categories,
            StartDate = task.StartDate?.Add(shift),
            ReminderAtUtc = task.ReminderAtUtc?.Add(shift),
            Recurrence = rule.ForNextOccurrence().ToRule()
        };

        return next;
    }
}
