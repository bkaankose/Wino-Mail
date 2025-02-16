using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI
{
    /// <summary>
    /// Emitted when synchronizer state is updated.
    /// </summary>
    /// <param name="synchronizer">Account Synchronizer</param>
    /// <param name="newState">New state.</param>
    public record AccountSynchronizerStateChanged(Guid AccountId, AccountSynchronizerState NewState) : UIMessageBase<AccountSynchronizerStateChanged>;
}
