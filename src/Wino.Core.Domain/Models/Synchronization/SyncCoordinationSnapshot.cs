using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>How one account receives live updates right now. <see cref="IsCovered"/> lets the timer skip it.</summary>
public sealed record AccountLiveCoverage(Guid AccountId, LiveCoverageKind Kind, LiveCoverageGap Gap, DateTimeOffset SinceUtc)
{
    public bool IsCovered => Gap == LiveCoverageGap.None;
}

/// <summary>
/// The coordinator's immutable view: the effective sync mode, the raw socket state, whether a flap cool-down
/// is holding the app in fallback, and the per-account coverage. Replaced as a whole on every change.
/// </summary>
public sealed record SyncCoordinationSnapshot(
    EffectiveSyncMode Mode,
    bool IsSocketConnected,
    bool IsFlapCooldownActive,
    DateTimeOffset ChangedUtc,
    IReadOnlyDictionary<Guid, AccountLiveCoverage> Coverage)
{
    public static SyncCoordinationSnapshot Initial(EffectiveSyncMode mode, DateTimeOffset now)
        => new(mode, false, false, now, new Dictionary<Guid, AccountLiveCoverage>());
}

/// <summary>The IDLE loop state of one desktop IMAP account, with the reason for the last transition.</summary>
public sealed record ImapIdleStatus(ImapIdleState State, string? Reason, DateTimeOffset ChangedUtc)
{
    public static ImapIdleStatus NeverStarted { get; } = new(ImapIdleState.Stopped, "not started", DateTimeOffset.MinValue);
}
