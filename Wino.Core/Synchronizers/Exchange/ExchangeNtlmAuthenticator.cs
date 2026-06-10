using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>Password credential provider for on-premises Exchange.</summary>
public class ExchangeNtlmAuthenticator : IExchangeAuthenticator
{
    public MailProviderType ProviderType => MailProviderType.Exchange;

    public Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account)
    {
        var serverInformation = account?.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        ExchangeCredentials credentials = new WebCredentials(
            serverInformation.IncomingServerUsername,
            serverInformation.IncomingServerPassword);

        return Task.FromResult(credentials);
    }
}
