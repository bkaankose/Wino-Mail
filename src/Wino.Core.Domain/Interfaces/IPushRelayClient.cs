#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// HTTP client for WinoRelay <c>/v1</c>. Every call sends the relay token as a bearer; device-scoped calls also send
/// <c>X-Device-Id</c>. Failures throw <see cref="Exceptions.PushRelayApiException"/>. A rejected or expired token is
/// renewed and the call retried once; <c>device_revoked</c> is surfaced to the caller.
/// </summary>
public interface IPushRelayClient
{
    Task<PushRelayWellKnown> GetWellKnownAsync(CancellationToken cancellationToken = default);
    Task<PushRelayRegisterDeviceResponse> RegisterDeviceAsync(PushRelayRegisterDeviceRequest request, CancellationToken cancellationToken = default);
    Task<PushRelayDevice> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<PushRelayDevice> UpdateDeviceAsync(Guid deviceId, PushRelayUpdateDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Signs this device out of the relay. Must run before the Wino tokens are discarded.</summary>
    Task DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<PushRelayPushTestResponse> SendPushTestAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<PushRelaySyncAccountsResponse> SyncAccountsAsync(Guid deviceId, List<string> clientAccountIds, CancellationToken cancellationToken = default);
    Task<PushRelayMailbox> CreateMailboxAsync(Guid deviceId, PushRelayCreateMailboxRequest request, CancellationToken cancellationToken = default);
    Task<PushRelayMailbox> UpdateMailboxCredentialsAsync(Guid deviceId, Guid subscriptionId, PushRelayUpdateCredentialsRequest request, CancellationToken cancellationToken = default);
    Task DeleteMailboxAsync(Guid deviceId, Guid subscriptionId, CancellationToken cancellationToken = default);
    Task<PushRelayResyncResponse> ResyncMailboxAsync(Guid deviceId, Guid subscriptionId, CancellationToken cancellationToken = default);
    Task<PushRelayMailboxList> ListMailboxesAsync(Guid deviceId, CancellationToken cancellationToken = default);
}
