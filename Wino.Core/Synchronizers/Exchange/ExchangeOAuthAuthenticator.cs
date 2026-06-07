using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// OAuth bearer credential provider for on-premises Exchange (production modern auth).
/// Reuses the durable refresh token stored on the account's <see cref="CustomServerInformation"/>
/// to mint short-lived access tokens via the generic <see cref="IOidcTokenClient"/>, caching the
/// access token in memory and persisting rotated refresh tokens. On refresh failure it surfaces an
/// auth error rather than downgrading — the account is flagged for interactive re-auth.
/// </summary>
public sealed class ExchangeOAuthAuthenticator : IExchangeAuthenticator
{
    // Refresh slightly ahead of true expiry so a token never lapses mid-request.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly IOidcTokenClient _oidcTokenClient;
    private readonly IAccountService _accountService;
    private readonly ExchangeTokenCache _tokenCache;

    public ExchangeOAuthAuthenticator(IOidcTokenClient oidcTokenClient, IAccountService accountService, ExchangeTokenCache tokenCache)
    {
        _oidcTokenClient = oidcTokenClient;
        _accountService = accountService;
        _tokenCache = tokenCache;
    }

    public MailProviderType ProviderType => MailProviderType.Exchange;

    public async Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account)
    {
        var server = account?.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var accessToken = await GetValidAccessTokenAsync(account.Id, server).ConfigureAwait(false);
        return new OAuthCredentials(accessToken);
    }

    /// <summary>
    /// Maps the account's stored OAuth fields to a generic OIDC configuration.
    /// </summary>
    public static OidcConfiguration BuildConfiguration(CustomServerInformation server) => new()
    {
        Authority = server.OAuthAuthority,
        ClientId = server.OAuthClientId,
        Resource = server.OAuthResource,
        RedirectUri = server.OAuthRedirectUri,
    };

    private async Task<string> GetValidAccessTokenAsync(Guid accountId, CustomServerInformation server)
    {
        if (_tokenCache.TryGet(accountId, out var cached) && cached.IsAccessTokenValid(ExpirySkew))
            return cached.AccessToken;

        if (string.IsNullOrEmpty(server.OAuthRefreshToken))
            throw new ExchangeInteractiveSignInRequiredException(
                "Exchange OAuth authentication requires interactive sign-in (no refresh token is stored).");

        var configuration = BuildConfiguration(server);
        var discovery = await _oidcTokenClient.GetDiscoveryDocumentAsync(configuration.Authority).ConfigureAwait(false);

        OidcTokenSet refreshed;
        try
        {
            refreshed = await _oidcTokenClient.RefreshAsync(discovery, configuration, server.OAuthRefreshToken).ConfigureAwait(false);
        }
        catch (OidcTokenException ex)
        {
            throw new ExchangeInteractiveSignInRequiredException(
                "Exchange OAuth authentication failed; the refresh token was rejected. Interactive sign-in required.", ex);
        }

        _tokenCache.Set(accountId, refreshed);

        // ExSTS / AD FS rotate the refresh token on each refresh; persist the new one so the next
        // app session can still authenticate silently.
        if (!string.IsNullOrEmpty(refreshed.RefreshToken) && refreshed.RefreshToken != server.OAuthRefreshToken)
        {
            server.OAuthRefreshToken = refreshed.RefreshToken;
            await _accountService.UpdateAccountCustomServerInformationAsync(server).ConfigureAwait(false);
        }

        return refreshed.AccessToken;
    }
}
