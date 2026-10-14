#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>What the settings page shows about live notifications.</summary>
public sealed record PushRelayRegistrationSnapshot(
    PushRelayAvailability Availability,
    bool IsOptInPlatform,
    bool IsUserOptedIn,
    Guid? DeviceId,
    bool HasPushToken,
    DateTime? LastCheckInUtc,
    DateTime? NextAttemptUtc,
    string? LastErrorCode,
    string? LastErrorDetail,
    List<PushRelayMailboxSnapshot> Mailboxes,
    int MailboxLimit,
    DateTime? LastTestPushUtc)
{
    public static PushRelayRegistrationSnapshot Initial(PushRelayAvailability availability, bool isOptInPlatform, bool isUserOptedIn)
        => new(availability, isOptInPlatform, isUserOptedIn, null, false, null, null, null, null, [], PushRelayDefaults.DefaultMailboxLimit, null);
}

/// <summary>One local account's relay subscription as the settings page shows it.</summary>
public sealed record PushRelayMailboxSnapshot(
    Guid AccountId,
    Guid? SubscriptionId,
    string? Status,
    bool Muted,
    string? MonitoringReason,
    string? LastErrorCode)
{
    public bool IsWatched => SubscriptionId is not null;
    public bool NeedsReauthentication => Status == PushRelayMailboxStatuses.NeedsReauth
        || LastErrorCode == PushRelayErrorCodes.ProviderAuthFailed;
}
