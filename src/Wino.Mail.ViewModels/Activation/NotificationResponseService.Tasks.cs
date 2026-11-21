#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Notifications;
using Wino.Mail.ViewModels.Helpers;

namespace Wino.Mail.ViewModels.Activation;

public sealed partial class NotificationResponseService
{
    private async Task HandleTaskResponseAsync(NotificationResponse response, Guid taskId)
    {
        if (response.ActionIdentifier == NotificationActionIdentifiers.TaskComplete)
        {
            if (IsActionRefusedByAppLock("task complete")) return;

            using var actionScope = appLock.AllowActionsWhileLocked();
            await CompleteTaskAsync(taskId);
            return;
        }

        if (response.ActionIdentifier == NotificationActionIdentifiers.TaskSnooze)
        {
            if (IsActionRefusedByAppLock("task snooze")) return;

            var minutes = ResolveSnoozeMinutes(response.Values, preferencesService.TaskReminderSnoozeMinutes);
            await taskService.SnoozeTaskReminderAsync(taskId, DateTime.UtcNow.AddMinutes(minutes));
            return;
        }

        if (response.IsOpen)
            await navigator.OpenModeAsync(WinoApplicationMode.Tasks);
    }

    /// <summary>Completes the task through the request pipeline, as the To Do page does.</summary>
    private async Task CompleteTaskAsync(Guid taskId)
    {
        var task = await taskService.GetTaskAsync(taskId);
        if (task is null || task.IsCompleted)
        {
            _logger.Information("Task notification {Id} has no open task to complete.", taskId);
            return;
        }

        var plan = await TaskCompletionPlanner.BuildAsync(task, isCompleted: true, taskService);
        await requestDelegator.ExecuteAsync(task.MailAccountId, TaskCompletionPlanner.Requests(plan));
    }
}
