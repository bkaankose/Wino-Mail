#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The refresh tokens of modern-auth Exchange accounts, one per account. Kept apart from the generic OAuth token
/// store so Exchange sign-in never shares state with IMAP OAuth. Access tokens are never persisted.
/// </summary>
public interface IExchangeTokenStore
{
    Task<string?> GetRefreshTokenAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SaveRefreshTokenAsync(Guid accountId, string refreshToken, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default);
}
