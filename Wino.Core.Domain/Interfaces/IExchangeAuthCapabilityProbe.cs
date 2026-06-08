using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Probes an Exchange endpoint for its modern-auth capability AND the OAuth authority, mirroring how
/// Outlook bootstraps on-prem modern auth: an unauthenticated request carrying the mailbox identity
/// (X-AnchorMailbox) provokes a 401 whose <c>WWW-Authenticate: Bearer</c> challenge includes the
/// <c>authorization_uri</c> and <c>issuer_kind</c>. The anchor mailbox is required — without it the
/// server returns only a generic reactive challenge with no authority.
/// </summary>
public interface IExchangeAuthCapabilityProbe
{
    Task<ExchangeAuthProbeResult> ProbeAsync(string ewsUrl, string emailAddress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an Exchange auth probe: the detected capability plus, when modern auth is offered, the
/// discovered OAuth authority derived from the challenge's <c>authorization_uri</c>.
/// </summary>
public sealed class ExchangeAuthProbeResult
{
    public ExchangeAuthCapability Capability { get; init; }

    /// <summary>OIDC authority derived from the challenge (e.g. <c>https://adfs.example.com/adfs</c>); null if not advertised.</summary>
    public string Authority { get; init; }

    /// <summary>Raw <c>authorization_uri</c> from the Bearer challenge; null if absent.</summary>
    public string AuthorizationUri { get; init; }

    /// <summary>Issuer flavor from the challenge (e.g. <c>ADFS</c>, <c>AAD</c>); null if absent.</summary>
    public string IssuerKind { get; init; }

    public static ExchangeAuthProbeResult Unknown { get; } = new() { Capability = ExchangeAuthCapability.Unknown };
}
