using System;
using Wino.Domain.Enums;

namespace Wino.Messaging.Client.Synchronization
{
    public record AccountSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result, Guid? SynchronizationTrackingId);
}
