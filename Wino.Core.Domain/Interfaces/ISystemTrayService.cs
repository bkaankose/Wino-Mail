using System;

namespace Wino.Core.Domain.Interfaces;

public interface ISystemTrayService
{
    /// <summary>
    /// Initializes the system tray icon.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Shows the system tray icon.
    /// </summary>
    void Show();

    /// <summary>
    /// Hides the system tray icon.
    /// </summary>
    void Hide();

    /// <summary>
    /// Event fired when the tray icon is double-clicked.
    /// </summary>
    event EventHandler? TrayIconDoubleClicked;

    /// <summary>
    /// Gets whether the tray icon is currently minimized.
    /// </summary>
    bool IsMinimizedToTray { get; }

    /// <summary>
    /// Disposes of the system tray resources.
    /// </summary>
    void Dispose();
}