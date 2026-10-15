using Android.App;
using Android.Content;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>
/// Android does not tell an app about other apps' full-screen or presentation state, so "presenting" is never detected.
/// Quiet time is Do Not Disturb: any interruption filter other than "all" (or unknown).
/// </summary>
public sealed class AndroidUserPresenceStateProvider(Context context) : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;

    public bool IsSystemQuietTimeActive()
        => context.GetSystemService(Context.NotificationService) is NotificationManager manager
            && manager.CurrentInterruptionFilter is not (InterruptionFilter.All or InterruptionFilter.Unknown);
}
