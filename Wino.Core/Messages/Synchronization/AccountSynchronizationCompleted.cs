using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Messages.Synchronization
{
    public record AccountSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result, Guid? SynchronizationTrackingId);
}
