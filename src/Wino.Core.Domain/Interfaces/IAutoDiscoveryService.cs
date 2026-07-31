using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.AutoDiscovery;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Searches for auto-discovery settings for custom mail accounts.
/// </summary>
public interface IAutoDiscoveryService
{
    /// <summary>
    /// Tries to return the best mail server settings using different techniques.
    /// </summary>
    Task<AutoDiscoverySettings> GetAutoDiscoverySettings(AutoDiscoveryMinimalSettings autoDiscoveryMinimalSettings);

    /// <summary>
    /// Tries to resolve a CalDAV endpoint for the mailbox address.
    /// </summary>
    Task<Uri> DiscoverCalDavServiceUriAsync(string mailAddress, CancellationToken cancellationToken = default);
}
