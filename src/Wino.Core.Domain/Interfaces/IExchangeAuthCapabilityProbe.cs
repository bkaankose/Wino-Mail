#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Asks an Exchange endpoint which authentication it offers and, for modern auth, which authority issues its tokens.</summary>
public interface IExchangeAuthCapabilityProbe
{
    /// <summary>Never throws except on cancellation; anything else reads as <see cref="ExchangeAuthProbeResult.Unknown"/>.</summary>
    Task<ExchangeAuthProbeResult> ProbeAsync(string ewsUrl, string? emailAddress, CancellationToken cancellationToken = default);
}
