using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>
/// Reports back the account synchronization progress.
/// </summary>
public record AccountSynchronizationProgressUpdatedMessage(AccountSynchronizationProgress Progress)
    : UIMessageBase<AccountSynchronizationProgressUpdatedMessage>;
