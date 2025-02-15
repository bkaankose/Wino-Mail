using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record AccountSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result, Guid? SynchronizationTrackingId)
    : UIMessageBase<AccountSynchronizationCompleted>;
