using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace Wino.Authentication;

/// <summary>Provider host construction, cache storage and interactive presentation.</summary>
public interface IOutlookAuthenticationHost
{
    IPublicClientApplication Client { get; }
    bool CanAuthenticateInteractively { get; }
    Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationResult> AcquireTokenInteractiveAsync(
        IEnumerable<string> scopes, IAccount account, string loginHint,
        CancellationToken cancellationToken = default);
    Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default);
}
