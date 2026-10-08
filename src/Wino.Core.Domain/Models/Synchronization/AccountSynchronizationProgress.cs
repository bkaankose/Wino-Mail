using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

public record AccountSynchronizationProgress(
    Guid AccountId,
    SynchronizationProgressCategory Category,
    bool IsInProgress,
    bool IsIndeterminate,
    double ProgressPercentage,
    int TotalUnits,
    int RemainingUnits,
    string Status,
    AccountSynchronizerState State)
{
    public int CompletedUnits => Math.Max(0, TotalUnits - RemainingUnits);

    public static AccountSynchronizationProgress Idle(Guid accountId, SynchronizationProgressCategory category)
        => new(
            accountId,
            category,
            false,
            false,
            0,
            0,
            0,
            string.Empty,
            AccountSynchronizerState.Idle);
}
