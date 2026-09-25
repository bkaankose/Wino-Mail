#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public interface ILocalIntelligenceService
{
    Task<IReadOnlyList<DailyBriefingAccount>> GetEligibleAccountsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Briefing entries for one local day, newest first: messages received that day and messages
    /// whose dated smart actions cover it. Only messages Classification included are returned.
    /// </summary>
    Task<DailyBriefingFactsResult> GetBriefingFactsAsync(
        DateOnly day,
        TimeZoneInfo timeZone,
        bool includeIgnored = false,
        CancellationToken cancellationToken = default);

    /// <summary>Ignores a card for the content it currently has.</summary>
    Task IgnoreBriefingItemAsync(Guid localAccountId, string remoteMessageId, string contentHash,
        CancellationToken cancellationToken = default);

    Task UnignoreBriefingItemAsync(Guid localAccountId, string remoteMessageId,
        CancellationToken cancellationToken = default);

    Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task InvalidateAccessSnapshotsAsync(CancellationToken cancellationToken = default);
    Task<DailyBriefingUnseenState> GetUnseenStateAsync(CancellationToken cancellationToken = default);
    Task MarkOpenedAsync(CancellationToken cancellationToken = default);
    Task MarkViewedAsync(CancellationToken cancellationToken = default);
    Task<bool> ShouldAutomaticallyProcessAsync(Guid localAccountId, CancellationToken cancellationToken = default);
}
