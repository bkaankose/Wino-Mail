namespace Wino.Core.Domain.Enums;

/// <summary>The authentication methods an Exchange (EWS) endpoint offers, as its challenge reports them.</summary>
public enum ExchangeAuthCapability
{
    /// <summary>Could not be determined: unreachable, TLS failure or an unexpected response.</summary>
    Unknown,

    /// <summary>Only Negotiate, NTLM or Basic were offered.</summary>
    BasicOnly,

    /// <summary>The endpoint advertises Bearer with an authorization server, so modern auth is available.</summary>
    ModernAuthAvailable
}
