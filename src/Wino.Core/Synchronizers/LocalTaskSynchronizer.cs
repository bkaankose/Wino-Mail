using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Tasks;

namespace Wino.Core.Synchronizers;

/// <summary>
/// Marker synchronizer for IMAP-family accounts. Local tasks are persisted by
/// <see cref="ITaskService"/> and this type intentionally has no HTTP dependency.
/// </summary>
public sealed class LocalTaskSynchronizer
{
    private readonly ITaskService _taskService;

    public LocalTaskSynchronizer(ITaskService taskService = null)
    {
        _taskService = taskService;
    }

    public async Task ExecuteRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_taskService is null)
            throw new InvalidOperationException("Local task persistence is unavailable.");

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request is not TaskActionRequest taskRequest)
                throw new NotSupportedException($"Local task request {request.GetType().Name} is not supported.");

            switch (taskRequest.Operation)
            {
                case Domain.Enums.TaskSynchronizerOperation.CreateList:
                case Domain.Enums.TaskSynchronizerOperation.UpdateList:
                    await _taskService.CompleteListMutationAsync(taskRequest.List.Id, taskRequest.List, false).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.DeleteList:
                    await _taskService.CompleteListMutationAsync(taskRequest.List.Id, null, true).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.CreateTask:
                case Domain.Enums.TaskSynchronizerOperation.UpdateTask:
                    await _taskService.CompleteTaskMutationAsync(taskRequest.Task.Id, taskRequest.Task, false).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.DeleteTask:
                    await _taskService.CompleteTaskMutationAsync(taskRequest.Task.Id, null, true).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.CreateStep:
                case Domain.Enums.TaskSynchronizerOperation.UpdateStep:
                    await _taskService.CompleteStepMutationAsync(taskRequest.Step.Id, taskRequest.Step, false).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.DeleteStep:
                    await _taskService.CompleteStepMutationAsync(taskRequest.Step.Id, null, true).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.CreateGroup:
                case Domain.Enums.TaskSynchronizerOperation.UpdateGroup:
                    await _taskService.CompleteTaskListGroupMutationAsync(taskRequest.Group.Id, taskRequest.Group, false).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.DeleteGroup:
                    await _taskService.CompleteTaskListGroupMutationAsync(taskRequest.Group.Id, null, true).ConfigureAwait(false);
                    break;
                case Domain.Enums.TaskSynchronizerOperation.UpdateListPlacement:
                    await _taskService.CompleteTaskListPlacementMutationAsync(taskRequest.List.Id, taskRequest.List).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Local task operation {taskRequest.Operation} is not supported.");
            }
        }
    }

    public Task<TaskSynchronizationResult> SynchronizeAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TaskSynchronizationResult.Empty);
    }
}
