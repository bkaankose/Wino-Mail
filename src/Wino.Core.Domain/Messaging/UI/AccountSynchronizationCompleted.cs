using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Messaging.UI;

public record AccountSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result, Guid? SynchronizationTrackingId, MailSynchronizationType Type)
    : UIMessageBase<AccountSynchronizationCompleted>;
