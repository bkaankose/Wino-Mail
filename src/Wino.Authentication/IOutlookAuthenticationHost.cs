using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace Wino.Authentication;

/// <summary>
/// Provider host construction, cache storage and interactive presentation. Hosts sign in with one client
/// (<see cref="Wino.Core.Domain.Models.Accounts.OutlookClientIds.Current"/>) unless they also keep a
/// <see cref="LegacyClientId"/> and override the client id overloads.
/// </summary>
public interface IOutlookAuthenticationHost
{
    IPublicClientApplication Client { get; }
    bool CanAuthenticateInteractively { get; }
    Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationResult> AcquireTokenInteractiveAsync(
        IEnumerable<string> scopes, IAccount account, string loginHint,
        CancellationToken cancellationToken = default);
    Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default);

    /// <summary>The client that accounts without a stored client id keep using; null when the host has only <see cref="Client"/>.</summary>
    string LegacyClientId => null;

    // Single-client hosts have no legacy client, so every account resolves to the id their Client was built with.
    IPublicClientApplication GetClient(string clientId) => Client;

    Task EnsureTokenCacheAttachedAsync(string clientId, CancellationToken cancellationToken = default)
        => EnsureTokenCacheAttachedAsync(cancellationToken);

    Task<AuthenticationResult> AcquireTokenInteractiveAsync(
        string clientId, IEnumerable<string> scopes, IAccount account, string loginHint,
        CancellationToken cancellationToken = default)
        => AcquireTokenInteractiveAsync(scopes, account, loginHint, cancellationToken);

    Task RemoveLocalAccountAsync(string clientId, string authenticationAddress, CancellationToken cancellationToken = default)
        => RemoveLocalAccountAsync(authenticationAddress, cancellationToken);
}
