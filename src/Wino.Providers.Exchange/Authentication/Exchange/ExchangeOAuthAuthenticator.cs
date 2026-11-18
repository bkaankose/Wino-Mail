#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication.Exchange;

/// <summary>
/// Bearer access tokens for modern-auth Exchange accounts (ADFS, Hybrid Modern Auth), minted from the refresh token in
/// <see cref="IExchangeTokenStore"/>. It never signs in interactively: the first sign-in and any sign-in after the
/// refresh token is rejected belong to the embedded browser sign-in, which stores the refresh token it receives.
/// </summary>
public sealed class ExchangeOAuthAuthenticator
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly IOAuthProtocolClient _protocolClient;
    private readonly IExchangeTokenStore _tokenStore;
    private readonly ExchangeTokenCache _tokenCache;

    public ExchangeOAuthAuthenticator(IOAuthProtocolClient protocolClient, IExchangeTokenStore tokenStore, ExchangeTokenCache tokenCache)
    {
        _protocolClient = protocolClient;
        _tokenStore = tokenStore;
        _tokenCache = tokenCache;
    }

    public async Task<string> GetAccessTokenAsync(MailAccount account, CustomServerInformation serverInformation, CancellationToken cancellationToken = default)
    {
        if (_tokenCache.TryGet(account.Id, out var cached) && cached.IsValid(ExpirySkew))
            return cached.AccessToken;

        var refreshLock = _tokenCache.GetRefreshLock(account.Id);
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenCache.TryGet(account.Id, out var fresh) && fresh.IsValid(ExpirySkew))
                return fresh.AccessToken;

            // Always the stored token, never one a caller holds: with a rotating issuer an older copy has already been
            // spent, and presenting it again can revoke the whole grant.
            var refreshToken = await _tokenStore.GetRefreshTokenAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken))
                throw new ExchangeInteractiveSignInRequiredException(account, "The Exchange account has no stored refresh token.");

            var authority = serverInformation.OAuthAuthority;
            var clientId = serverInformation.OAuthClientId;
            if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId))
                throw new ExchangeInteractiveSignInRequiredException(account, "The Exchange account has no OAuth authority or client id.");

            OAuthTokenResult refreshed;
            try
            {
                var metadata = await _protocolClient.GetMetadataAsync(authority, cancellationToken).ConfigureAwait(false);
                var endpoints = _protocolClient.CreateEndpoints(authority, metadata, OAuthTrustSource.UserEntered);
                refreshed = await _protocolClient
                    .RefreshAsync(endpoints.TokenEndpoint, clientId, refreshToken, GetResource(serverInformation), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OAuthProtocolException ex) when (!ex.IsTransient)
            {
                throw new ExchangeInteractiveSignInRequiredException(account, "The identity provider rejected the Exchange refresh token.", ex);
            }

            _tokenCache.Set(account.Id, new ExchangeAccessToken(refreshed.AccessToken, refreshed.ExpiresAtUtc));

            // The issuer has already spent the old token, so the rotated one is saved even if the caller gave up.
            if (!string.IsNullOrEmpty(refreshed.RefreshToken) && !string.Equals(refreshed.RefreshToken, refreshToken, StringComparison.Ordinal))
                await _tokenStore.SaveRefreshTokenAsync(account.Id, refreshed.RefreshToken, CancellationToken.None).ConfigureAwait(false);

            return refreshed.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>The configured resource, or the Exchange server's origin, which is what on-premises Exchange accepts.</summary>
    public static string? GetResource(CustomServerInformation serverInformation)
    {
        if (!string.IsNullOrWhiteSpace(serverInformation.OAuthResource))
            return serverInformation.OAuthResource.Trim();

        return Uri.TryCreate(serverInformation.IncomingServer, UriKind.Absolute, out var server)
            ? server.GetLeftPart(UriPartial.Authority) + "/"
            : null;
    }
}
