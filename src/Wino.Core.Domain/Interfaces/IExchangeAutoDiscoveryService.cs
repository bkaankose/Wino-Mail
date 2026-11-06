#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Finds the EWS endpoint of an on-premises Exchange mailbox from its email address alone.</summary>
public interface IExchangeAutoDiscoveryService
{
    /// <summary>
    /// The mailbox's EWS URL, or null when discovery finds none and the user has to enter it. Only cancellation throws.
    /// </summary>
    Task<string?> TryDiscoverEwsUrlAsync(string emailAddress, CancellationToken cancellationToken = default);
}
