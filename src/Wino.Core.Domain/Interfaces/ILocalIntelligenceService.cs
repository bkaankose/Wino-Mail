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
    Task<DailyBriefingFactsResult> GetBriefingFactsAsync(DateOnly localDate, TimeZoneInfo timeZone,
        bool includeIgnored = false, CancellationToken cancellationToken = default);
    Task IgnoreBriefingItemAsync(Guid localAccountId, Guid briefingId, long artifactRevision,
        CancellationToken cancellationToken = default);
    Task UnignoreBriefingItemAsync(Guid localAccountId, Guid briefingId,
        CancellationToken cancellationToken = default);

    /// <summary>Latest locally imported briefing-fact revision for the account.</summary>
    Task<long> GetLatestBriefingFactRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task InvalidateAccessSnapshotsAsync(CancellationToken cancellationToken = default);
    Task<DailyBriefingUnseenState> GetUnseenStateAsync(CancellationToken cancellationToken = default);
    Task MarkOpenedAsync(CancellationToken cancellationToken = default);
    Task MarkViewedAsync(CancellationToken cancellationToken = default);
    Task<bool> ShouldAutomaticallyProcessAsync(Guid localAccountId, CancellationToken cancellationToken = default);
}
