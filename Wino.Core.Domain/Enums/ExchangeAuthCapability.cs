namespace Wino.Core.Domain.Enums;

/// <summary>
/// Result of probing an Exchange (EWS) endpoint for the authentication methods it offers.
/// </summary>
public enum ExchangeAuthCapability
{
    /// <summary>Could not determine (unreachable, TLS failure, unexpected response).</summary>
    Unknown,

    /// <summary>Only legacy schemes (Negotiate/NTLM/Basic) were offered.</summary>
    BasicOnly,

    /// <summary>The endpoint advertises Bearer / OAuth — modern auth is available.</summary>
    ModernAuthAvailable
}
