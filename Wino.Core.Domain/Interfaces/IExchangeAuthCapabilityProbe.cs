using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Probes an Exchange EWS endpoint to detect whether it offers modern auth (OAuth) so onboarding
/// can default the auth method intelligently. On-prem Exchange with a non-EvoSTS (e.g. ADFS) auth
/// server does not advertise Bearer on a bare request, but answers a presented token with a
/// <c>WWW-Authenticate: Bearer</c> challenge — that reactive challenge is the detection signal.
/// </summary>
public interface IExchangeAuthCapabilityProbe
{
    Task<ExchangeAuthCapability> ProbeAsync(string ewsUrl, CancellationToken cancellationToken = default);
}
