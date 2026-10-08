using AppKit;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

public sealed class MacTaskCompletionSound(IDispatcher dispatcher) : ITaskCompletionSound
{
    public void Play() => _ = dispatcher.ExecuteOnUIThread(() => AppKitFramework.NSBeep());
}

/// <summary>
/// Never reports presenting or quiet time. macOS Focus modes already hold back notifications
/// that Wino Mail posts, so the app relies on the system rather than reading the Focus state
/// (which needs a separate entitlement and user authorization).
/// </summary>
public sealed class MacUserPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;
    public bool IsSystemQuietTimeActive() => false;
}
