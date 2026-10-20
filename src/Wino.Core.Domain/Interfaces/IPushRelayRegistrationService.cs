#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Keeps this device registered with WinoRelay for the signed-in Wino Account: device, push token, account list and
/// mailbox credentials. All work runs on one coalescing worker with persisted backoff.
/// </summary>
public interface IPushRelayRegistrationService
{
    PushRelayRegistrationSnapshot Snapshot { get; }

    /// <summary>Raised on a background thread after every pass and every reported status change.</summary>
    event EventHandler<PushRelayRegistrationSnapshot>? SnapshotChanged;

    bool IsUserOptedIn { get; }

    /// <summary>Subscribes to account and push-token changes, starts the hourly check-in and requests a startup pass.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues a pass. Reasons that arrive while a pass runs are coalesced into one more pass.</summary>
    void RequestSync(PushRelaySyncReason reason);

    /// <summary>Runs a pass now and returns when it finishes.</summary>
    Task SyncNowAsync(PushRelaySyncReason reason, CancellationToken cancellationToken = default);

    /// <summary>Stores the user's choice on opt-in platforms, then registers or signs the device out.</summary>
    Task SetUserOptInAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>A short check-in for hosts that wake the app in the background.</summary>
    Task CheckInAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the relay device (bounded, best effort) and forgets the local identity and state. Called before the
    /// Wino tokens are discarded; relay failures never escape it.
    /// </summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the account's own provider registration (the Graph subscription) so the provider stops notifying the
    /// relay. Called before the account and its tokens are deleted; bounded, and failures never escape it.
    /// </summary>
    Task ReleaseAccountAsync(MailAccount account, CancellationToken cancellationToken = default);

    Task<PushRelayPushTestResult> SendPushTestAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a status a push reported for one account. <c>needs_reauth</c> also forgets the uploaded fingerprint.</summary>
    void ReportMailboxStatus(Guid accountId, string? status, string? reason, string? message);

    /// <summary>Records that a <c>test</c> push arrived.</summary>
    void ReportTestPush();
}
