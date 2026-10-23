using System;

namespace MailCal.Messaging.UI;

/// <summary>
/// Sent when all queued synchronization requests for an account have been executed.
/// </summary>
public record SynchronizationActionsCompleted(
    Guid AccountId) : UIMessageBase<SynchronizationActionsCompleted>;
