using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messages.Client.Synchronization
{
    public record AccountSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result, Guid? SynchronizationTrackingId);
}
