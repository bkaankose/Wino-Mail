#nullable enable
using System;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>Fixed values for the WinoRelay live-notification service.</summary>
public static class PushRelayDefaults
{
    /// <summary>Used when the relay token response carries no <c>relayUrl</c>.</summary>
    public const string DefaultBaseUrl = "https://relay.winomail.app/";

    /// <summary>Shown until a relay token reports the account's <c>wr_mbx</c> claim.</summary>
    public const int DefaultMailboxLimit = 10;

    /// <summary>The configuration key of the user's opt-in on opt-in platforms (non-roaming).</summary>
    public const string OptInConfigurationKey = "PushRelay.OptIn";

    /// <summary>The configuration key of the persisted <see cref="PushRelayState"/> JSON (non-roaming).</summary>
    public const string StateConfigurationKey = "PushRelay.State";

    /// <summary>The privacy policy version relay-based sync requires. Bumping it re-prompts desktop users once.</summary>
    public const int PolicyVersion = 1;

    /// <summary>The configuration key of the accepted <see cref="PolicyVersion"/> (non-roaming).</summary>
    public const string PolicyAcceptedVersionConfigurationKey = "PushRelay.PolicyAcceptedVersion";

    /// <summary>The configuration key of the pending desktop introduction window (non-roaming).</summary>
    public const string IntroductionPendingConfigurationKey = "PushRelay.IntroductionPending";

    /// <summary>Longest device name and OS version the relay accepts.</summary>
    public const int MaxDeviceTextLength = 64;

    /// <summary>A relay token is renewed this long before it expires.</summary>
    public static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(2);
}

/// <summary>Values of the relay's <c>platform</c> field.</summary>
public static class PushRelayPlatforms
{
    public const string Ios = "ios";
    public const string MacOS = "macos";
    public const string Android = "android";

    /// <summary>Desktop like <see cref="MacOS"/>: receives events over the relay socket, with WNS as the push channel.</summary>
    public const string Windows = "windows";
}

/// <summary>Values of the relay's <c>provider</c> field.</summary>
public static class PushRelayProviders
{
    public const string Imap = "imap";
    public const string Gmail = "gmail";
    public const string Graph = "graph";
}

/// <summary>Values of the relay's IMAP <c>security</c> field.</summary>
public static class PushRelayImapSecurity
{
    public const string Ssl = "ssl";
    public const string StartTls = "starttls";
    public const string None = "none";
}

/// <summary>Values of a relay mailbox <c>status</c>.</summary>
public static class PushRelayMailboxStatuses
{
    public const string Active = "active";
    public const string NeedsReauth = "needs_reauth";
    public const string Error = "error";
}

/// <summary>Values of the push payload <c>t</c> key.</summary>
public static class PushRelayPushKind
{
    public const string MailboxChanged = "mailbox.changed";
    public const string MailboxStatus = "mailbox.status";
    public const string MailboxReauth = "mailbox.reauth";
    public const string MailboxError = "mailbox.error";
    public const string MailboxRenew = "mailbox.renew";
    public const string Test = "test";
}

/// <summary>Keys of the data-only push payload.</summary>
public static class PushRelayPayloadKeys
{
    public const string Version = "v";
    public const string Kind = "t";

    /// <summary>The relay event id (<c>eid</c>), acknowledged over the socket or <c>POST /v1/devices/me/events/ack</c>.</summary>
    public const string EventId = "eid";

    /// <summary>The changed folder (<c>f</c>) when the relay knows it.</summary>
    public const string Folder = "f";
    public const string SubscriptionId = "sid";
    public const string ClientAccountId = "cid";
    public const string MailboxId = "mid";
    public const string Reason = "r";
    public const string Status = "st";
    public const string MonitoringReason = "mr";
    public const string MonitoringMessage = "mm";
    public const string Retryable = "rt";
}
