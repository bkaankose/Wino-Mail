#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Reads the Google account's address with an access token, so a Gmail sign-in can be checked against the accounts
/// Wino already has before anything is saved. The token always carries the userinfo.email scope.
/// </summary>
public interface IGoogleMailboxProfileService
{
    /// <summary>The account's primary address, or null when Google did not return one. Only cancellation throws.</summary>
    Task<string?> GetMailboxAddressAsync(string accessToken, CancellationToken cancellationToken = default);
}
