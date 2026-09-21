using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Resolves the EWS endpoint URL for an email address via anonymous Autodiscover V2,
/// so onboarding can auto-fill the EWS URL from just the address.
/// </summary>
public interface IExchangeAutoDiscoveryService
{
    /// <summary>
    /// Attempts to discover the account's EWS URL. Returns null when discovery is unavailable
    /// (the user can still enter the URL manually).
    /// </summary>
    Task<string> TryDiscoverEwsUrlAsync(string emailAddress, CancellationToken cancellationToken = default);
}
