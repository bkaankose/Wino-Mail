using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Proves the native MAPI/HTTP path to an account's mailbox from inside the app, with the
/// credentials the app actually holds, and asks Autodiscover which transport the mailbox offers.
/// </summary>
public interface IMapiConnectionProbe
{
    Task<MapiProbeResult> ProbeAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks Autodiscover which transport the mailbox offers: MapiHttp when a mapiHttp protocol is
    /// advertised, Ews when Autodiscover answers without one, Automatic (unknown) when it cannot be
    /// reached at all, so a transient outage never locks an account onto the older transport.
    /// </summary>
    Task<ExchangeTransport> DetectTransportAsync(MailAccount account, CancellationToken cancellationToken = default);
}
