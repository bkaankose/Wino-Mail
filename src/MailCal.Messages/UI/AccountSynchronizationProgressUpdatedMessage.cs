using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Messaging.UI;

/// <summary>
/// Reports back the account synchronization progress.
/// </summary>
public record AccountSynchronizationProgressUpdatedMessage(AccountSynchronizationProgress Progress)
    : UIMessageBase<AccountSynchronizationProgressUpdatedMessage>;
