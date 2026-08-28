using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Services;

/// <summary>
/// Projects <see cref="SynchronizationManager"/>'s per-account progress into the single
/// state the shell title bar button renders. Every mode goes through here instead of
/// keeping its own copy, so the button and the account menu items can never disagree
/// about what is running.
/// </summary>
public static class ShellSynchronizationStateReader
{
    /// <summary>
    /// Aggregates the live progress of <paramref name="accountIds"/> in one category.
    /// Any account in progress makes the whole thing busy; a single account without a unit
    /// count makes the aggregate unmeasurable, because a percentage that ignores it would
    /// finish while work is still running.
    /// </summary>
    public static ShellSynchronizationState Read(IEnumerable<Guid> accountIds, SynchronizationProgressCategory category)
    {
        if (accountIds == null)
            return ShellSynchronizationState.Idle;

        var isSynchronizing = false;
        var isIndeterminate = false;
        var totalUnits = 0;
        var completedUnits = 0;

        foreach (var accountId in accountIds)
        {
            AccountSynchronizationProgress progress;

            try
            {
                progress = SynchronizationManager.Instance.GetSynchronizationProgress(accountId, category);
            }
            catch (InvalidOperationException)
            {
                // The manager has not been initialized yet. Nothing can be running.
                return ShellSynchronizationState.Idle;
            }

            if (progress?.IsInProgress != true)
                continue;

            isSynchronizing = true;

            if (progress.IsIndeterminate || progress.TotalUnits <= 0)
            {
                isIndeterminate = true;
                continue;
            }

            totalUnits += progress.TotalUnits;
            completedUnits += progress.CompletedUnits;
        }

        if (!isSynchronizing)
            return ShellSynchronizationState.Idle;

        if (isIndeterminate || totalUnits <= 0)
            return new ShellSynchronizationState(true, true, 0d);

        var percentage = Math.Clamp((double)completedUnits / totalUnits * 100d, 0d, 100d);

        return new ShellSynchronizationState(true, false, percentage);
    }
}
