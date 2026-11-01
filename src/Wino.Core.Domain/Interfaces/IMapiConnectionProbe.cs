#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Asks Autodiscover, with the credentials the app actually holds, which transport an on-premises Exchange
/// mailbox offers.
/// </summary>
public interface IMapiConnectionProbe
{
    /// <summary>
    /// MapiHttp when Autodiscover advertises MAPI over HTTP, Ews when it answers without it, and Automatic when it
    /// cannot be reached, so a transient outage never locks an account onto the older transport.
    /// </summary>
    Task<ExchangeTransport> DetectTransportAsync(MailAccount account, CancellationToken cancellationToken = default);
}
