#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// Per-device relay state, persisted as JSON in non-roaming configuration. It never holds tokens or passwords:
/// push tokens are stored as a hash and credentials as a fingerprint.
/// </summary>
public sealed class PushRelayState
{
    /// <summary>The user's choice on opt-in platforms. Kept when the rest of the state is cleared.</summary>
    public bool UserOptIn { get; set; }

    public DateTime? LastCheckInUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public int FailureCount { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }

    /// <summary>SHA-256 of the push token last sent to the relay; empty string when none was sent.</summary>
    public string? LastPushTokenHash { get; set; }
    public string? LastAppVersion { get; set; }
    public string? LastAppBuild { get; set; }
    public string? LastLocale { get; set; }
    public string? LastName { get; set; }
    public string? LastOsVersion { get; set; }

    /// <summary>SHA-256 of the socket public key last sent to the relay (desktop); empty string when none was sent.</summary>
    public string? LastSocketKeyHash { get; set; }

    /// <summary>The relay refused this platform. Retried once the app version changes.</summary>
    public bool PlatformRejected { get; set; }
    public string? PlatformRejectedAppVersion { get; set; }

    public DateTime? LastTestPushUtc { get; set; }
    public int? MailboxLimit { get; set; }

    /// <summary>Keyed by the local account id (the relay's <c>clientAccountId</c>).</summary>
    public Dictionary<Guid, PushRelayMailboxRegistration> Mailboxes { get; set; } = [];
}

/// <summary>The relay subscription of one local account on this device.</summary>
public sealed class PushRelayMailboxRegistration
{
    public Guid? SubscriptionId { get; set; }

    /// <summary>Fingerprint of the credentials last sent (or last attempted, when <see cref="LastErrorCode"/> is set).</summary>
    public string? CredentialFingerprint { get; set; }
    public string? Status { get; set; }
    public bool Muted { get; set; }
    public string? MonitoringReason { get; set; }
    public string? MonitoringMessage { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
