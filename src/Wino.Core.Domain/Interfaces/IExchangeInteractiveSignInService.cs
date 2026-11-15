#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Interactive sign-in for modern-auth Exchange accounts (ADFS, Hybrid Modern Auth): an authorization code request
/// with PKCE in the embedded browser, whose refresh token is saved to <see cref="IExchangeTokenStore"/>. Only
/// user-initiated flows call it; background synchronization raises
/// <see cref="Exceptions.ExchangeInteractiveSignInRequiredException"/> instead.
/// </summary>
public interface IExchangeInteractiveSignInService
{
    /// <summary>False on hosts without an <see cref="IEmbeddedBrowserAuthBroker"/>.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Signs in with the authority, client id, resource and redirect address in <paramref name="serverInformation"/>
    /// and stores the refresh token under <paramref name="accountId"/>. Account setup passes the id of the account it
    /// is about to create, and deletes the stored token if it abandons that account.
    /// </summary>
    /// <exception cref="Exceptions.AccountSetupCanceledException">The user closed the sign-in page.</exception>
    /// <exception cref="Exceptions.ExchangeSignInException">The sign-in is unsupported here or failed.</exception>
    Task<ExchangeSignInResult> SignInAsync(
        Guid accountId,
        CustomServerInformation serverInformation,
        string? loginHint,
        CancellationToken cancellationToken = default);
}
