using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>Routes Exchange accounts to OAuth or password credentials.</summary>
public sealed class ExchangeAuthenticator : IExchangeAuthenticator
{
    private readonly ExchangeNtlmAuthenticator _ntlmAuthenticator;
    private readonly ExchangeOAuthAuthenticator _oauthAuthenticator;

    public ExchangeAuthenticator(ExchangeNtlmAuthenticator ntlmAuthenticator, ExchangeOAuthAuthenticator oauthAuthenticator)
    {
        _ntlmAuthenticator = ntlmAuthenticator;
        _oauthAuthenticator = oauthAuthenticator;
    }

    public MailProviderType ProviderType => MailProviderType.Exchange;

    public Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account)
    {
        var server = account?.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        return server.UseOAuthAuthentication
            ? _oauthAuthenticator.GetCredentialsAsync(account)
            : _ntlmAuthenticator.GetCredentialsAsync(account);
    }
}
