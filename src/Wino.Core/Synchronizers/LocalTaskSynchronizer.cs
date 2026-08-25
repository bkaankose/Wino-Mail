using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers;

/// <summary>
/// Marker synchronizer for IMAP-family accounts. Local tasks are persisted by
/// <see cref="ITaskService"/> and this type intentionally has no HTTP dependency.
/// </summary>
public sealed class LocalTaskSynchronizer
{
    public Task ExecuteRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<TaskSynchronizationResult> SynchronizeAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TaskSynchronizationResult.Empty);
    }
}
