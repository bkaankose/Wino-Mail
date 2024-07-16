using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messages.Client.Synchronization
{
    /// <summary>
    /// Emitted when synchronizer state is updated.
    /// </summary>
    /// <param name="synchronizer">Account Synchronizer</param>
    /// <param name="newState">New state.</param>
    public record AccountSynchronizerStateChanged(IBaseSynchronizer Synchronizer, AccountSynchronizerState NewState);
}
