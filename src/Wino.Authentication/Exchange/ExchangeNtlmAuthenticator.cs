using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Authentication.Exchange;

/// <summary>Password credential provider for on-premises Exchange.</summary>
public sealed class ExchangeNtlmAuthenticator
{
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
