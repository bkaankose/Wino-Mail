using System;

namespace Wino.Messaging.UI
{
    /// <summary>
    /// Reports back the account synchronization progress.
    /// </summary>
    public record AccountSynchronizationProgressUpdatedMessage(Guid AccountId, double Progress) : UIMessageBase<AccountSynchronizationProgressUpdatedMessage>;
}
