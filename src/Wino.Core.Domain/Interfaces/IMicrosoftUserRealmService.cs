#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Asks Microsoft whether an address belongs to a Microsoft 365 (Entra ID) tenant. The typed address leaves the device,
/// so callers only use it when the domain's MX points at a known mail filter (<see cref="KnownMailHosts.IsMailFilterHost"/>).
/// Never throws for network failures; only cancellation throws.
/// </summary>
public interface IMicrosoftUserRealmService
{
    Task<MicrosoftUserRealm> GetUserRealmAsync(string emailAddress, CancellationToken cancellationToken = default);
}
