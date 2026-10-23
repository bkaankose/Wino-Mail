using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record AccountSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result, Guid? SynchronizationTrackingId, MailSynchronizationType Type)
    : UIMessageBase<AccountSynchronizationCompleted>;
