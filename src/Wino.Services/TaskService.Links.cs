using System;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Links;

namespace Wino.Services;

public sealed partial class TaskService
{
    /// <summary>
    /// Commits a task change the provider confirmed. A confirmed create stores the links that waited for it; a
    /// confirmed delete removes the task's links.
    /// </summary>
    public async Task CompleteTaskMutationAsync(
        Guid taskId,
        AccountTask remoteTask,
        bool deleted,
        AccountTask requestedTask = null,
        TaskSynchronizerOperation? completedOperation = null)
    {
        await CompleteTaskMutationCoreAsync(taskId, remoteTask, deleted, requestedTask, completedOperation).ConfigureAwait(false);

        if (_itemLinks is null)
            return;

        try
        {
            if (deleted)
            {
                _itemLinks.DiscardPendingLinks(taskId);
                await _itemLinks.DeleteLinksForItemAsync(ItemLinkKind.Task, [ItemLinkKeys.ForTask(taskId)]).ConfigureAwait(false);
                return;
            }

            // A create can be adopted by a row the provider reconciliation stored first, under its own id.
            var stored = await GetTaskAsync(taskId).ConfigureAwait(false)
                ?? await FindTaskByRemoteIdAsync(remoteTask).ConfigureAwait(false);
            if (stored is not null)
                await _itemLinks.ConfirmPendingLinksAsync(taskId, ItemLinkKeys.ForTask(stored)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not update the links of task {TaskId}.", taskId);
        }
    }
}
