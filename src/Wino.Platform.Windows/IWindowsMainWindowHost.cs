using System;
using System.Threading.Tasks;

namespace Wino.Platform.Windows;

/// <summary>
/// The application's main window, for operating-system UI that must be parented to it (Store purchase, rating and
/// update consent). The head implements it; the window type stays there.
/// </summary>
public interface IWindowsMainWindowHost
{
    bool IsMainWindowAvailable { get; }

    /// <summary>
    /// Runs the operation on the main window's thread with the window's handle.
    /// </summary>
    /// <exception cref="InvalidOperationException">The main window or its dispatcher is not available.</exception>
    Task<T> RunWithMainWindowHandleAsync<T>(Func<nint, Task<T>> operation);
}
