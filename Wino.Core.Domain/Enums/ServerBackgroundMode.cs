namespace Wino.Core.Domain.Enums;

/// <summary>
/// What should happen to server app when the client is terminated.
/// </summary>
public enum ServerBackgroundMode
{
    MinimizedTray, // Still runs, tray icon is visible.
    Invisible, // Still runs, tray icon is invisible.
    Terminate // Server is terminated as Wino terminates.
}
