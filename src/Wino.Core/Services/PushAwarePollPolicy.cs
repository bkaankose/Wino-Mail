using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Services;

/// <summary>What one tick of a periodic synchronization loop should do for an account.</summary>
public enum PollDecision
{
    /// <summary>Run the loop's ordinary poll.</summary>
    Poll,

    /// <summary>Do nothing: the push channel is delivering the changes.</summary>
    Skip,

    /// <summary>Run a full reconciliation, the safety net for anything push did not deliver.</summary>
    Reconcile
}

/// <summary>
/// Relaxes periodic polling for an account while its push channel is open. Push delivers changes as
/// they happen, so the poll is skipped and only every <see cref="BackstopEveryNTicks"/>th tick runs a
/// full reconciliation as a safety net. The poll tightens again as soon as the channel is reported
/// closed, and a channel that closed and came back since the last tick costs one reconciliation,
/// because nothing was pushed while it was down. Accounts without a push channel are never passed in.
/// </summary>
public sealed class PushAwarePollPolicy
{
    public const int DefaultBackstopEveryNTicks = 10;

    private readonly ConcurrentDictionary<Guid, AccountState> _states = new();

    public PushAwarePollPolicy(int backstopEveryNTicks = DefaultBackstopEveryNTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(backstopEveryNTicks, 1);

        BackstopEveryNTicks = backstopEveryNTicks;
    }

    public int BackstopEveryNTicks { get; }

    /// <param name="accountId">The account the tick is for.</param>
    /// <param name="isPushConnected">Whether the account's push channel is open right now.</param>
    /// <param name="interruptionCount">How many times the channel has closed so far.</param>
    public PollDecision Next(Guid accountId, bool isPushConnected, int interruptionCount)
    {
        if (!_states.TryGetValue(accountId, out var state))
        {
            // The first tick for an account always polls: nothing is known about what it missed.
            _states[accountId] = new AccountState(isPushConnected, interruptionCount, 0);
            return PollDecision.Poll;
        }

        if (!isPushConnected)
        {
            _states[accountId] = new AccountState(false, interruptionCount, 0);

            // Tighten: the tick that finds the channel closed catches up on what push missed, and the
            // ordinary poll carries on from there until the channel is back.
            return state.WasConnected ? PollDecision.Reconcile : PollDecision.Poll;
        }

        var missedChanges = !state.WasConnected || state.InterruptionCount != interruptionCount;
        var relaxedTicks = state.RelaxedTicks + 1;

        if (missedChanges || relaxedTicks >= BackstopEveryNTicks)
        {
            _states[accountId] = new AccountState(true, interruptionCount, 0);
            return PollDecision.Reconcile;
        }

        _states[accountId] = new AccountState(true, interruptionCount, relaxedTicks);
        return PollDecision.Skip;
    }

    /// <summary>Drops the state of accounts that no longer exist.</summary>
    public void Retain(IEnumerable<Guid> accountIds)
    {
        var current = accountIds.ToHashSet();

        foreach (var accountId in _states.Keys.Where(id => !current.Contains(id)).ToList())
            _states.TryRemove(accountId, out _);
    }

    private readonly record struct AccountState(bool WasConnected, int InterruptionCount, int RelaxedTicks);
}
