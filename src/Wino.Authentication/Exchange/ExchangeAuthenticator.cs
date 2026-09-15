using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authentication;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Authentication.Exchange;

/// <summary>Routes Exchange accounts to OAuth or password credentials.</summary>
public sealed class ExchangeAuthenticator : IExchangeAuthenticator
{
    private readonly ExchangeNtlmAuthenticator _ntlmAuthenticator;
    private readonly ExchangeOAuthAuthenticator _oauthAuthenticator;
    private readonly ExchangeTokenCache _tokenCache;

    public ExchangeAuthenticator(ExchangeNtlmAuthenticator ntlmAuthenticator, ExchangeOAuthAuthenticator oauthAuthenticator, ExchangeTokenCache tokenCache)
    {
        _ntlmAuthenticator = ntlmAuthenticator;
        _oauthAuthenticator = oauthAuthenticator;
        _tokenCache = tokenCache;
    }

    public MailProviderType ProviderType => MailProviderType.Exchange;

    public Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account)
    {
        var authAccount = RequireServerInformation(account);

        return authAccount.ServerInformation.UseOAuthAuthentication
            ? _oauthAuthenticator.GetCredentialsAsync(authAccount)
            : _ntlmAuthenticator.GetCredentialsAsync(authAccount);
    }

    public Task<string> TryGetBearerTokenAsync(MailAccount account)
    {
        var authAccount = RequireServerInformation(account);

        return authAccount.ServerInformation.UseOAuthAuthentication
            ? _oauthAuthenticator.TryGetBearerTokenAsync(authAccount)
            : Task.FromResult<string>(null);
    }

    /// <summary>
    /// The generic token surface: a bearer token for OAuth accounts, or an empty token for password accounts
    /// (their credentials never leave <see cref="GetCredentialsAsync"/>). Interactive sign-in lives in the
    /// Exchange settings page, so a missing refresh token surfaces as
    /// <see cref="ExchangeInteractiveSignInRequiredException"/> rather than a prompt here.
    /// </summary>
    public async Task<TokenInformationEx> GetTokenInformationAsync(MailAccount account, IReadOnlyCollection<ProviderFeature> requiredFeatures = null)
    {
        var token = await TryGetBearerTokenAsync(account).ConfigureAwait(false);

        return new TokenInformationEx(token ?? string.Empty, account.Address, account.Address);
    }

    public Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account, IReadOnlyCollection<ProviderFeature> requestedFeatures = null)
        => GetTokenInformationAsync(account, requestedFeatures);

    public Task DeleteTokenInformationAsync(MailAccount account)
    {
        if (account != null)
            _tokenCache.Remove(account.Id);

        return Task.CompletedTask;
    }

    // The returned account always has ServerInformation (throws otherwise), so callers can dereference it directly.
    private static MailAccount RequireServerInformation(MailAccount account)
    {
        if (account?.ServerInformation == null)
            throw new InvalidOperationException("Exchange account is missing server information.");

        return account;
    }
}
