#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Provides cached Wino Account Intelligence metadata with coalesced background revalidation,
/// and the entitlement evaluated from it for the signed-in account.
/// </summary>
public interface IWinoAccountIntelligenceSnapshotService
{
    Task<WinoAccountIntelligenceSnapshot?> GetCachedAsync(Guid winoAccountId, CancellationToken cancellationToken = default);
    Task<WinoAccountIntelligenceRefreshResult?> RefreshAsync(CancellationToken cancellationToken = default);
    Task<WinoAccountIntelligenceRefreshResult?> RefreshPurchasesAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>The last evaluated entitlement. Signed out until the first evaluation.</summary>
    WinoIntelligenceEntitlementSnapshot CurrentEntitlement { get; }

    /// <summary>Evaluates the entitlement from the cached snapshot without contacting the server.</summary>
    Task<WinoIntelligenceEntitlementSnapshot> GetEntitlementAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes the snapshot from the server and evaluates the entitlement from the result.</summary>
    Task<WinoIntelligenceEntitlementSnapshot> RefreshEntitlementAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the entitlement to signed out immediately.</summary>
    void SetSignedOut();
}
