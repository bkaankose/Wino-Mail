using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// iOS does not let an app see other apps' windows, so "presenting" is never detected. Focus modes already hold back
/// Wino's notifications at the system level, and reading the Focus state needs a separate entitlement and user
/// authorization, so quiet time is not reported either. While Wino itself is in the foreground, the notification
/// center delegate decides how a notification is shown.
/// </summary>
public sealed class IOSUserPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;
    public bool IsSystemQuietTimeActive() => false;
}
