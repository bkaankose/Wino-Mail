using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// NTLM/Negotiate credential provider for on-premises Exchange.
/// Reads credentials from the account's <see cref="CustomServerInformation"/>
/// (same place IMAP stores them). Used for the Phase 0 spike; production uses the
/// OAuth/ExSTS implementation behind the same <see cref="IExchangeAuthenticator"/> seam.
/// </summary>
public class ExchangeNtlmAuthenticator : IExchangeAuthenticator
{
    public MailProviderType ProviderType => MailProviderType.Exchange;

    public Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account)
    {
        var serverInformation = account?.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        // Domain may be supplied as DOMAIN\\user inside the username, or left to Negotiate.
        ExchangeCredentials credentials = new WebCredentials(
            serverInformation.IncomingServerUsername,
            serverInformation.IncomingServerPassword);

        return Task.FromResult(credentials);
    }
}
