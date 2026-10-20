namespace Wino.Core.Domain.Enums;

/// <summary>
/// How the application learns about mailbox changes. An app-wide choice on desktop; mobile always uses the relay.
/// </summary>
public enum SyncMethod
{
    /// <summary>
    /// Periodic synchronization on the user's intervals. Gmail and Outlook notifications are not instant; desktop IMAP
    /// accounts keep their own IDLE connection in this mode too, as before the relay existed.
    /// </summary>
    Timer,

    /// <summary>
    /// WinoRelay pushes changes (socket on desktop, APNs and FCM on mobile). On desktop the <c>ISyncCoordinator</c>
    /// decides per account whether live coverage exists (relay mailbox over a stable socket, or IMAP IDLE); accounts
    /// without it, and every account while the socket is down, sync on the user's intervals. Contacts and To Do keep
    /// their timer-based rhythm.
    /// </summary>
    Relay
}
