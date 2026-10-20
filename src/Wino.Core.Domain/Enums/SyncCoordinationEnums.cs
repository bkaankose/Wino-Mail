namespace Wino.Core.Domain.Enums;

/// <summary>
/// How mailbox changes reach the app right now, as decided by <see cref="Interfaces.ISyncCoordinator"/>.
/// Desktop chooses between <see cref="Timer"/> and <see cref="Relay"/>; the coordinator degrades Relay to
/// <see cref="RelayFallback"/> while live coverage is not trustworthy. Mobile always reads <see cref="PlatformPush"/>.
/// </summary>
public enum EffectiveSyncMode
{
    /// <summary>Periodic synchronization on the user's intervals; no relay. Desktop IMAP IDLE keeps running alongside, as it always has.</summary>
    Timer,

    /// <summary>Relay-based sync is selected and the desktop socket is stable; covered accounts skip their timer ticks.</summary>
    Relay,

    /// <summary>Relay-based sync is selected but the socket is down, flapping or the registration is unavailable; every account syncs on the timer.</summary>
    RelayFallback,

    /// <summary>Phones and tablets: APNs and FCM pushes plus the platform's background schedule. The timer loops keep their defaults.</summary>
    PlatformPush
}

/// <summary>What delivers live updates for one account.</summary>
public enum LiveCoverageKind
{
    None,

    /// <summary>Gmail or Outlook mailbox watched by the relay and delivered over the desktop socket.</summary>
    RelayMailbox,

    /// <summary>Desktop IMAP account with a listening IDLE connection.</summary>
    ImapIdle
}

/// <summary>Why an account is not covered live. <see cref="None"/> means the timer tick may be skipped.</summary>
public enum LiveCoverageGap
{
    None,
    TimerMode,
    PlatformPush,
    ProviderUnsupported,
    SocketDown,
    FlapCooldown,
    MailboxNotWatched,
    MailboxNeedsReauth,
    MailboxError,
    IdleUnsupported,
    IdleNotRunning,
    AccountAttention
}

/// <summary>Why the coordinator asks the runtime for one bounded mail synchronization outside the timer.</summary>
public enum SyncCatchUpReason
{
    /// <summary>Live coverage was lost and the last automatic sync is older than the mail interval.</summary>
    FallbackEntered,

    /// <summary>Live coverage returned after an outage long enough to have missed changes.</summary>
    Recovered,

    /// <summary>The user switched from relay-based to timer-based sync; accounts that were skipping ticks sync now.</summary>
    SyncMethodChanged
}

/// <summary>User-visible moments at which a stalled relay registration or socket deserves another attempt.</summary>
public enum RelayRetryTrigger
{
    NetworkRestored,
    Foreground,
    SettingsOpened
}

/// <summary>The state of a desktop IMAP account's IDLE loop.</summary>
public enum ImapIdleState
{
    /// <summary>No loop runs; <c>Reason</c> on the status says why it ended.</summary>
    Stopped,

    /// <summary>The inbox is being watched; changes arrive without the timer.</summary>
    Listening,

    /// <summary>The connection dropped and the loop is waiting to reconnect.</summary>
    Reconnecting
}

/// <summary>What the last relay socket disconnect means for recovery.</summary>
public enum RelaySocketOutageKind
{
    /// <summary>The socket is connected, was never started, or closed on purpose (stopped, or handed over to the device's new owner).</summary>
    None,

    /// <summary>The transport dropped or the relay is restarting; the client reconnects on its own.</summary>
    Transient,

    /// <summary>The relay refused, revoked, replaced or banned this socket; the client stays down until something changes.</summary>
    Terminal
}
