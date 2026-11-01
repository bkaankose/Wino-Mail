#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Credentials for on-premises Exchange accounts: bearer tokens minted from the account's stored refresh token, or
/// the stored password. It never shows UI; a modern-auth account without a usable refresh token raises
/// <see cref="Exceptions.ExchangeInteractiveSignInRequiredException"/>.
/// </summary>
public interface IExchangeAuthenticator : IAuthenticator
{
    Task<ExchangeCredential> GetCredentialAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>A bearer access token for modern-auth accounts, or null for password accounts.</summary>
    Task<string?> TryGetBearerTokenAsync(MailAccount account, CancellationToken cancellationToken = default);
}
