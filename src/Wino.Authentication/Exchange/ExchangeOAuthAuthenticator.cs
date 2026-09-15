using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Authentication.Oidc;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Authentication.Exchange;

/// <summary>OAuth bearer credential provider for on-premises Exchange.</summary>
public sealed class ExchangeOAuthAuthenticator
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly IOidcTokenClient _oidcTokenClient;
    // Resolved lazily: AccountService depends on IAuthenticationProvider, which hands out this
    // authenticator, so taking IAccountService in the constructor would close a dependency cycle.
    private readonly IServiceProvider _serviceProvider;
    private readonly ExchangeTokenCache _tokenCache;

    public ExchangeOAuthAuthenticator(IOidcTokenClient oidcTokenClient, IServiceProvider serviceProvider, ExchangeTokenCache tokenCache)
    {
        _oidcTokenClient = oidcTokenClient;
        _serviceProvider = serviceProvider;
        _tokenCache = tokenCache;
    }

    public async Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account)
    {
        var server = account?.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var accessToken = await GetValidAccessTokenAsync(account.Id, server).ConfigureAwait(false);
        return new OAuthCredentials(accessToken);
    }

    public async Task<string> TryGetBearerTokenAsync(MailAccount account)
    {
        var server = account?.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        return await GetValidAccessTokenAsync(account.Id, server).ConfigureAwait(false);
    }

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

        // Serialize refresh per account so two concurrent callers can't both spend the (single-use) refresh token.
        var refreshLock = _tokenCache.GetRefreshLock(accountId);
        await refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have refreshed while we waited for the lock.
            if (_tokenCache.TryGet(accountId, out var fresh) && fresh.IsAccessTokenValid(ExpirySkew))
                return fresh.AccessToken;

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

            // The setup page probes with a transient account (not persisted); only rotate the stored token
            // for an account that exists, otherwise the write would create an orphaned server-information row.
            if (!string.IsNullOrEmpty(refreshed.RefreshToken) && refreshed.RefreshToken != server.OAuthRefreshToken)
            {
                server.OAuthRefreshToken = refreshed.RefreshToken;

                if (server.AccountId != Guid.Empty && _serviceProvider.GetService(typeof(IAccountService)) is IAccountService accountService)
                {
                    await accountService.UpdateAccountCustomServerInformationAsync(server).ConfigureAwait(false);
                }
            }

            return refreshed.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }
}
