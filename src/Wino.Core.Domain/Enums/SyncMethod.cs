namespace Wino.Core.Domain.Enums;

/// <summary>
/// How the application learns about mailbox changes. An app-wide choice on desktop; mobile always uses the relay.
/// </summary>
public enum SyncMethod
{
    /// <summary>Periodic synchronization on the user's intervals. Notifications are not instant.</summary>
    Timer,

    /// <summary>
    /// WinoRelay pushes changes (socket on desktop, APNs and FCM on mobile). Mail and calendar fall back to a fixed
    /// 5 minute timer while the desktop socket is down. Contacts and To Do keep their timer-based rhythm.
    /// </summary>
    Relay
}
