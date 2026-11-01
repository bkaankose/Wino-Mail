#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication.Exchange;

/// <summary>Routes Exchange accounts to OAuth bearer tokens or their stored password.</summary>
public sealed class ExchangeAuthenticator : IExchangeAuthenticator
{
    private readonly ExchangeNtlmAuthenticator _ntlmAuthenticator;
    private readonly ExchangeOAuthAuthenticator _oauthAuthenticator;
    private readonly ExchangeTokenCache _tokenCache;
    private readonly IExchangeTokenStore _tokenStore;

    public ExchangeAuthenticator(
        ExchangeNtlmAuthenticator ntlmAuthenticator,
        ExchangeOAuthAuthenticator oauthAuthenticator,
        ExchangeTokenCache tokenCache,
        IExchangeTokenStore tokenStore)
    {
        _ntlmAuthenticator = ntlmAuthenticator;
        _oauthAuthenticator = oauthAuthenticator;
        _tokenCache = tokenCache;
        _tokenStore = tokenStore;
    }

    public MailProviderType ProviderType => MailProviderType.Exchange;

    public async Task<ExchangeCredential> GetCredentialAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        var serverInformation = RequireServerInformation(account);

        if (!serverInformation.UseOAuthAuthentication)
            return _ntlmAuthenticator.GetCredential(account, serverInformation);

        var accessToken = await _oauthAuthenticator.GetAccessTokenAsync(account, serverInformation, cancellationToken).ConfigureAwait(false);
        return new ExchangeCredential.Bearer(accessToken);
    }

    public async Task<string?> TryGetBearerTokenAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        var serverInformation = RequireServerInformation(account);

        return serverInformation.UseOAuthAuthentication
            ? await _oauthAuthenticator.GetAccessTokenAsync(account, serverInformation, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// A bearer token for modern-auth accounts, or an empty token for password accounts, whose credentials only leave
    /// through <see cref="GetCredentialAsync"/>.
    /// </summary>
    public async Task<TokenInformationEx> GetTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature>? requiredFeatures = null,
        CancellationToken cancellationToken = default)
    {
        var token = await TryGetBearerTokenAsync(account, cancellationToken).ConfigureAwait(false);
        return new TokenInformationEx(token ?? string.Empty, account.Address, account.Address);
    }

    public async Task<TokenInformationEx> RefreshTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature>? requiredFeatures = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        _tokenCache.Remove(account.Id);
        return await GetTokenInformationAsync(account, requiredFeatures, cancellationToken).ConfigureAwait(false);
    }

    // Exchange's interactive sign-in is the embedded browser sign-in, which is not wired yet. Until it is, an account
    // without a usable refresh token raises ExchangeInteractiveSignInRequiredException here instead of prompting.
    public Task<TokenInformationEx> GenerateTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature>? requestedFeatures = null,
        CancellationToken cancellationToken = default)
        => GetTokenInformationAsync(account, requestedFeatures, cancellationToken);

    public async Task DeleteTokenInformationAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        _tokenCache.Remove(account.Id);
        await _tokenStore.DeleteAsync(account.Id, cancellationToken).ConfigureAwait(false);
    }

    private static CustomServerInformation RequireServerInformation(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return account.ServerInformation
            ?? throw new InvalidOperationException("The Exchange account has no server information.");
    }
}
