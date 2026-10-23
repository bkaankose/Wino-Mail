using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.AutoDiscovery;

namespace MailCal.Core.Domain.Interfaces;

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

    /// <summary>
    /// Tries to resolve a CardDAV endpoint for the mailbox address the same way (RFC 6764 SRV/TXT,
    /// then the well-known URI and <c>carddav.</c> host).
    /// </summary>
    Task<Uri> DiscoverCardDavServiceUriAsync(string mailAddress, CancellationToken cancellationToken = default);
}
