#nullable enable
using System;
using System.Net;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication.Exchange;

/// <summary>The stored password of an on-premises Exchange account as NTLM or Basic network credentials.</summary>
public sealed class ExchangeNtlmAuthenticator
{
    public ExchangeCredential GetCredential(MailAccount account, CustomServerInformation serverInformation)
    {
        var password = serverInformation.IncomingServerPassword;
        if (string.IsNullOrEmpty(password))
            throw new AuthenticationAttentionException(account);

        var userName = string.IsNullOrWhiteSpace(serverInformation.IncomingServerUsername)
            ? account.Address
            : serverInformation.IncomingServerUsername.Trim();

        if (string.IsNullOrWhiteSpace(userName))
            throw new InvalidOperationException("The Exchange account has neither a user name nor an address.");

        // Not every NTLM implementation splits DOMAIN\user itself, so the domain is passed separately.
        var separator = userName.IndexOf('\\');
        var credential = separator > 0
            ? new NetworkCredential(userName[(separator + 1)..], password, userName[..separator])
            : new NetworkCredential(userName, password);

        return new ExchangeCredential.Password(credential);
    }
}
