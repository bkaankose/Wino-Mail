#nullable enable
using System.Net;

namespace Wino.Core.Domain.Models.Authentication;

/// <summary>
/// What an on-premises Exchange transport authenticates with: the stored password as NTLM or Basic network
/// credentials, or an OAuth bearer access token. EWS and MAPI over HTTP map it to their own credential types.
/// </summary>
public abstract record ExchangeCredential
{
    private ExchangeCredential() { }

    public sealed record Password(NetworkCredential Credential) : ExchangeCredential;

    public sealed record Bearer(string AccessToken) : ExchangeCredential;
}
