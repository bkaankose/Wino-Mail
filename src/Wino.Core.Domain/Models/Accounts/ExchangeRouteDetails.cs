#nullable enable
namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// What address-first resolution learned about an on-premises Exchange mailbox: the EWS endpoint Autodiscover named
/// and how that endpoint asks clients to sign in.
/// </summary>
public sealed record ExchangeRouteDetails(string EwsUrl, ExchangeAuthProbeResult AuthProbe)
{
    /// <summary>The endpoint named an authorization server, so the account signs in with the organization's page.</summary>
    public bool IsModernAuth => AuthProbe.Capability == Enums.ExchangeAuthCapability.ModernAuthAvailable &&
                                !string.IsNullOrWhiteSpace(AuthProbe.Authority);
}
