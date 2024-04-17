using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Messages.Synchronization
{
    /// <summary>
    /// Triggers a new synchronization if possible.
    /// </summary>
    /// <param name="Options">Options for synchronization.</param>
    public record NewSynchronizationRequested(SynchronizationOptions Options);
}
