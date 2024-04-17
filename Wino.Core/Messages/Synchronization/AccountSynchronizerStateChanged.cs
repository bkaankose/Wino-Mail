using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers;

namespace Wino.Core.Messages.Synchronization
{
    /// <summary>
    /// Emitted when synchronizer state is updated.
    /// </summary>
    /// <param name="synchronizer">Account Synchronizer</param>
    /// <param name="newState">New state.</param>
    public record AccountSynchronizerStateChanged(IBaseSynchronizer Synchronizer, AccountSynchronizerState NewState);
}
