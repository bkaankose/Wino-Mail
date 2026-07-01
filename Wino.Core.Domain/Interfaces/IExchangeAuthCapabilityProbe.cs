using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Probes an Exchange endpoint for modern-auth capability and authority discovery.</summary>
public interface IExchangeAuthCapabilityProbe
{
    Task<ExchangeAuthProbeResult> ProbeAsync(string ewsUrl, string emailAddress, CancellationToken cancellationToken = default);
}

public sealed class ExchangeAuthProbeResult
{
    public ExchangeAuthCapability Capability { get; init; }

    public string Authority { get; init; }

    public string AuthorizationUri { get; init; }

    public string IssuerKind { get; init; }

    public static ExchangeAuthProbeResult Unknown { get; } = new() { Capability = ExchangeAuthCapability.Unknown };
}
