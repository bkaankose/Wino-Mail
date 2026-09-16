using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Asks Autodiscover, with the credentials the app actually holds, which transport an account's
/// mailbox offers.
/// </summary>
public interface IMapiConnectionProbe
{
    /// <summary>
    /// Asks Autodiscover which transport the mailbox offers: MapiHttp when a mapiHttp protocol is
    /// advertised, Ews when Autodiscover answers without one, Automatic (unknown) when it cannot be
    /// reached at all, so a transient outage never locks an account onto the older transport.
    /// </summary>
    Task<ExchangeTransport> DetectTransportAsync(MailAccount account, CancellationToken cancellationToken = default);
}
