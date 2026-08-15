#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Provides cached Wino Account Intelligence metadata and coalesced background revalidation.</summary>
public interface IWinoAccountIntelligenceSnapshotService
{
    Task<WinoAccountIntelligenceSnapshot?> GetCachedAsync(Guid winoAccountId, CancellationToken cancellationToken = default);
    Task<WinoAccountIntelligenceRefreshResult?> RefreshAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
