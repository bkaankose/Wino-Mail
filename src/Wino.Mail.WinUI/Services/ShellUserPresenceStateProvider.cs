using System;
using System.Runtime.InteropServices;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Reads the shell's notification state. There is no WinUI API for Focus assist, so this is the
/// supported Win32 route. The P/Invoke is kept in the app layer so notification policy stays
/// unit-testable against the interface.
/// </summary>
public sealed partial class ShellUserPresenceStateProvider : IUserPresenceStateProvider
{
    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out UserNotificationState state);

    public bool IsPresenting()
    {
        var state = GetState();

        return state is UserNotificationState.PresentationMode
            or UserNotificationState.RunningDirect3dFullScreen
            or UserNotificationState.Busy;
    }

    public bool IsSystemQuietTimeActive() => GetState() == UserNotificationState.QuietTime;

    private static UserNotificationState GetState()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 ? state : UserNotificationState.AcceptsNotifications;
        }
        catch (Exception)
        {
            // The shell call is unavailable in some session states. Treat it as "nothing special is
            // happening" rather than suppressing every notification.
            return UserNotificationState.AcceptsNotifications;
        }
    }
}
